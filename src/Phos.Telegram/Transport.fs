namespace Phos.Telegram

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading.Tasks
open Phos.Core.DomainTypes
open Phos.Core.Chunker
open FsToolkit.ErrorHandling

/// Telegram bot identity after login.
type BotInfo =
    { BotId: int64
      Username: string option }

/// A message entity with UTF-16 offsets, reusing the Core entity kinds.
type TelegramEntity =
    { Offset: int
      Length: int
      Kind: EntityKind
      Url: string option }

/// A message to send, with a stable MTProto `random_id`.
type SendTarget =
    { ChatId: ChatId
      RandomId: int64
      Text: string
      Entities: TelegramEntity list }

/// Result of sending a message.
type SendResult = { RemoteMessageId: int64 }

/// Expected Telegram-side send failures, typed so callers never parse exception
/// text.
///
/// `MissingPeer` is the retryable send-error case: the `InputPeer` (with access
/// hash) is not in the cache — the in-memory cache is empty after a restart
/// while durable outbox rows survive. The transport hydrates the peer before
/// surfacing it, so it means "hydration could not resolve the peer yet; the
/// delivery loop should try again later without consuming a retry attempt".
type SendError =
    | FloodWait of int
    | SlowModeWait of int
    | MissingPeer of ChatId
    | Other of string

/// Reference to a Telegram voice message to download.
type VoiceRef =
    { ChatId: ChatId
      MessageId: int64
      FileReference: byte[]
      AccessHash: int64 }

/// Reference to a Telegram photo to download.
type PhotoRef =
    { ChatId: ChatId
      MessageId: int64
      Photo: TL.Photo }

/// A short human-readable summary of a single Telegram message.
///
/// `[<RequireQualifiedAccess>]` keeps the `Text`/`Voice`/`Photo`/`Other` cases
/// out of unqualified scope: `Other` would otherwise clash with
/// `SendError.Other` and break every unqualified `SendError` match in the
/// solution. Consumers must write `MessageSummary.Text` etc.
[<RequireQualifiedAccess>]
type MessageSummary =
    | Text of string
    | Voice
    | Photo
    | Other

/// One entry in a fetched chat history, chronological oldest first.
type HistoryEntry =
    { Id: int64
      FromBot: bool
      Date: DateTimeOffset
      Summary: MessageSummary }

/// Thrown by `Transport.resolvePeer` when a chat's `InputPeer` (with access
/// hash) is not in the cache. This happens after a restart: the in-memory cache
/// is empty while durable inbox/outbox rows still reference those chats.
/// Consumers that can recover (the outbox delivery loop, the OMP worker scan)
/// catch this type and trigger `HydratePeers`; everyone else keeps the
/// well-known "no cached peer" failure. Typed so callers never parse exception
/// text.
type NoCachedPeerException(chat: ChatId) =
    inherit Exception(sprintf "no cached peer for %A" chat)

    /// The chat whose peer was missing.
    member _.ChatId = chat

/// Credentials and session location for the bot transport.
type TelegramConfig =
    { ApiId: int
      ApiHash: string
      BotToken: string
      SessionPath: string }

/// Pure transport helpers: config mapping, request building, peer caching and
/// response extraction. Kept separate from `TelegramTransport` so they are
/// testable without a live WTelegramClient connection.
module Transport =

    /// Maps WTelegramClient config keys to credentials. Only the four keys a bot
    /// needs are answered; phone/code/2FA keys return `null` and are never
    /// requested, so a bot-only login never enters the user auth flow.
    let configProvider (config: TelegramConfig) (key: string) : string | null =
        match key with
        | "api_id" -> config.ApiId.ToString()
        | "api_hash" -> config.ApiHash
        | "bot_token" -> config.BotToken
        | "session_pathname" -> config.SessionPath
        | _ -> null

    /// Sets a Unix file mode on a path, ignoring failures (e.g. non-POSIX FS).
    let private setUnixMode (path: string) (mode: UnixFileMode) : unit =
        try
            File.SetUnixFileMode(path, mode)
        with _ ->
            ()

    /// Creates the session directory (0700) for the given session path.
    let ensureSessionDir (sessionPath: string | null) : unit =
        match Path.GetDirectoryName(sessionPath) with
        | null -> ()
        | "" -> ()
        | dir ->
            Directory.CreateDirectory(dir) |> ignore
            setUnixMode dir (UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

    /// Maps a Core entity kind to the matching WTelegramClient message entity.
    let toTLMessageEntity (entity: TelegramEntity) : TL.MessageEntity =
        let result =
            match entity.Kind with
            | Bold -> TL.MessageEntityBold() :> TL.MessageEntity
            | Italic -> TL.MessageEntityItalic() :> TL.MessageEntity
            | Code -> TL.MessageEntityCode() :> TL.MessageEntity
            | Pre -> TL.MessageEntityPre() :> TL.MessageEntity
            | TextUrl ->
                let e = TL.MessageEntityTextUrl()

                e.url <-
                    (match entity.Url with
                     | Some u -> u
                     | None -> "")

                e :> TL.MessageEntity
            | Mention -> TL.MessageEntityMention() :> TL.MessageEntity
            | Hashtag -> TL.MessageEntityHashtag() :> TL.MessageEntity
            | Unknown -> TL.MessageEntityUnknown() :> TL.MessageEntity

        result.offset <- entity.Offset
        result.length <- entity.Length
        result

    /// Builds a `messages.sendMessage` request with an explicit random_id.
    let buildSendRequest (peer: TL.InputPeer) (target: SendTarget) : TL.Methods.Messages_SendMessage =
        let req = TL.Methods.Messages_SendMessage()
        req.peer <- peer
        req.message <- target.Text
        req.random_id <- target.RandomId

        if not (List.isEmpty target.Entities) then
            req.entities <- target.Entities |> List.map toTLMessageEntity |> List.toArray
            req.flags <- req.flags ||| TL.Methods.Messages_SendMessage.Flags.has_entities

        req

    /// Builds a `messages.getHistory` request for paged history before `beforeId`.
    ///
    /// `offset_id` is exclusive: the returned history starts just before the
    /// given message id. The remaining numeric fields are left at zero (no
    /// additional offset/filter), and `limit` is clamped to 1..100 as Telegram
    /// rejects limits outside that range.
    let buildGetHistoryRequest (peer: TL.InputPeer) (beforeId: int64) (limit: int) : TL.Methods.Messages_GetHistory =
        let req = TL.Methods.Messages_GetHistory()
        req.peer <- peer
        req.offset_id <- int beforeId
        req.offset_date <- DateTime.UnixEpoch
        req.add_offset <- 0
        req.limit <- min 100 (max 1 limit)
        req.max_id <- 0
        req.min_id <- 0
        req.hash <- 0L
        req

    /// Builds a `messages.editMessage` request.
    let buildEditRequest
        (peer: TL.InputPeer)
        (messageId: int64)
        (text: string)
        (entities: TelegramEntity list)
        : TL.Methods.Messages_EditMessage =
        let req = TL.Methods.Messages_EditMessage()
        req.peer <- peer
        req.id <- int messageId
        req.message <- text

        // `message` is an optional field gated by `flags.11` (has_message = 2048)
        // in the TL schema; with flags left at zero it is never serialized and
        // the edit silently no-ops. Always set it.
        req.flags <- req.flags ||| TL.Methods.Messages_EditMessage.Flags.has_message

        if not (List.isEmpty entities) then
            req.entities <- entities |> List.map toTLMessageEntity |> List.toArray
            req.flags <- req.flags ||| TL.Methods.Messages_EditMessage.Flags.has_entities

        req

    /// Builds a `messages.sendReaction` request.
    ///
    /// `reaction` is an optional field gated by `flags.0` in the TL schema: with
    /// `flags` left at zero the field is never serialized and Telegram silently
    /// ignores the whole request, so `has_reaction` is always set.
    let buildReactionRequest
        (peer: TL.InputPeer)
        (messageId: int64)
        (emoji: string)
        : TL.Methods.Messages_SendReaction =
        let req = TL.Methods.Messages_SendReaction()
        req.peer <- peer
        req.msg_id <- int messageId

        let reaction = TL.ReactionEmoji()
        reaction.emoticon <- emoji
        req.reaction <- [| reaction :> TL.Reaction |]
        req.flags <- TL.Methods.Messages_SendReaction.Flags.has_reaction
        req

    /// Builds a `messages.setTyping` request with a typing action.
    ///
    /// `action` is a mandatory field (not gated by a flags bit), so no flags
    /// need to be set; `top_msg_id` is left unset.
    let buildTypingRequest (peer: TL.InputPeer) : TL.Methods.Messages_SetTyping =
        let req = TL.Methods.Messages_SetTyping()
        req.peer <- peer
        req.action <- TL.SendMessageTypingAction() :> TL.SendMessageAction
        req

    /// Extracts the remote message id from the `messages.sendMessage` response.
    ///
    /// A bot send returns an `UpdatesBase`; for a text send the id lives on the
    /// `UpdateShortSentMessage` returned directly (an `UpdatesBase` subtype).
    let extractRemoteMessageId (result: TL.UpdatesBase) : int64 =
        match result with
        | :? TL.UpdateShortSentMessage as s -> int64 s.id
        | :? TL.UpdateShortMessage as s -> int64 s.id
        | _ -> 0L

    /// Maps a WTelegramClient transport exception to a typed `SendError`.
    /// A 420 `RpcException` carries the real wait seconds in `X` (the message is
    /// masked to e.g. "FLOOD_WAIT_X"), so it is preferred. Otherwise recognizes
    /// `FLOOD_WAIT_<n>` / `SLOWMODE_WAIT_<n>` (underscore optional) from the
    /// message text; everything else becomes `Other`.
    let mapRpcError (ex: exn) : SendError =
        match ex with
        | :? TL.RpcException as rpc when rpc.Code = 420 && rpc.X > 0 ->
            if rpc.Message.StartsWith "SLOWMODE" then
                SlowModeWait rpc.X
            else
                FloodWait rpc.X
        | _ ->
            let m = Regex.Match(ex.Message, "(FLOOD_WAIT|SLOWMODE_WAIT)_?(\d+)")

            if m.Success then
                let seconds = int m.Groups.[2].Value

                match m.Groups.[1].Value with
                | "FLOOD_WAIT" -> FloodWait seconds
                | _ -> SlowModeWait seconds
            else
                Other ex.Message

    /// Delay before retrying a failed bot login: exact FLOOD_WAIT seconds when the
    /// RpcException carries them (X), otherwise a fixed 30s backoff.
    let loginRetryDelay (ex: exn) : TimeSpan =
        match ex with
        | :? TL.RpcException as rpc when rpc.Code = 420 && rpc.X > 0 -> TimeSpan.FromSeconds(float rpc.X)
        | _ -> TimeSpan.FromSeconds 30.0

    /// Builds the bot identity from a logged-in `TL.User`.
    let extractBotInfo (userId: int64) (botUser: TL.User) : BotInfo =
        { BotId = userId
          Username = Option.ofObj botUser.MainUsername }

    /// Secures the session file (0600), ignoring failures.
    let secureSessionFile (sessionPath: string) : unit =
        try
            File.SetUnixFileMode(sessionPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        with _ ->
            ()

    /// Extracts the voice `Document` from a fetched message, if any.
    let tryVoiceDocument (message: TL.MessageBase option) : TL.Document option =
        match message with
        | Some(:? TL.Message as m) ->
            match m.media with
            | :? TL.MessageMediaDocument as media ->
                match media.document with
                | :? TL.Document as doc -> Some doc
                | _ -> None
            | _ -> None
        | _ -> None

    /// Classifies a fetched message into a short summary for history/reply display.
    let classifyMessage (m: TL.MessageBase) : MessageSummary =
        match m with
        | :? TL.Message as msg ->
            if not (String.IsNullOrWhiteSpace msg.message) then
                MessageSummary.Text msg.message
            else
                match msg.media with
                | :? TL.MessageMediaPhoto -> MessageSummary.Photo
                | :? TL.MessageMediaDocument when (tryVoiceDocument (Some(msg :> TL.MessageBase)) |> Option.isSome) ->
                    MessageSummary.Voice
                | _ -> MessageSummary.Other
        | _ -> MessageSummary.Other

    /// Resolves a chat to its `InputPeer` from the cache.
    let resolvePeer (cache: PeerCache) (chat: ChatId) : TL.InputPeer =
        match cache.Get chat with
        | Some peer -> peer
        | None -> raise (NoCachedPeerException chat)

    /// Populates the peer cache from an update's users/chats dictionaries.
    let cachePeers (cache: PeerCache) (updates: TL.UpdatesBase) : unit =
        for kv in updates.Users do
            let u = kv.Value

            if not (isNull u) then
                cache.CacheUser(u.id, u.access_hash)

        for kv in updates.Chats do
            match kv.Value with
            | :? TL.Channel as ch -> cache.CacheChannel(ch.id, ch.access_hash)
            | _ -> ()

    // --- Post-restart peer hydration -----------------------------------------

    /// Bulk hydrates users from the response of `users.getUsers` (or any user
    /// dictionary).
    ///
    /// A bot may pass `access_hash = 0` for users it has already interacted with
    /// (Telegram relaxes the access-hash requirement for bots, see the "Zero
    /// access hash" rule in https://core.telegram.org/api/peers#access-hash), so
    /// the returned full `User` constructors are cached as-is.
    let hydrateUsers (cache: PeerCache) (users: TL.UserBase seq) : unit =
        for u in users do
            match box u with
            | :? TL.User as user -> cache.CacheUser(user.id, user.access_hash)
            | _ -> ()

    /// Bulk hydrates chats from a chat-id dictionary (e.g. `channels.chats`):
    /// full `Channel` constructors carry their `access_hash`; basic groups are
    /// skipped (they need no access hash and are hydrated via
    /// `messages.getChats` + `cacheBasicChats`).
    let hydrateChats
        (cache: PeerCache)
        (chats: System.Collections.Generic.KeyValuePair<int64, TL.ChatBase> seq)
        : unit =
        for kv in chats do
            match kv.Value with
            | :? TL.Channel as ch -> cache.CacheChannel(ch.id, ch.access_hash)
            | _ -> ()

    /// Builds the `users.getUsers` request with zero access hashes.
    let buildGetUsersRequest (userIds: int64 list) : TL.Methods.Users_GetUsers =
        let req = TL.Methods.Users_GetUsers()

        req.id <-
            userIds
            |> List.map (fun uid -> TL.InputUser(uid, 0L) :> TL.InputUserBase)
            |> Array.ofList

        req

    /// Builds the `channels.getChannels` request. For bots a zero access hash is
    /// accepted for channels/supergroups already visible to the bot (same
    /// relaxation as for users).
    let buildGetChannelsRequest (channelIds: int64 list) : TL.Methods.Channels_GetChannels =
        let req = TL.Methods.Channels_GetChannels()

        req.id <-
            channelIds
            |> List.map (fun cid -> TL.InputChannel(cid, 0L) :> TL.InputChannelBase)
            |> Array.ofList

        req

    /// Builds the `messages.getChats` request for basic (legacy) groups. Bot
    /// allowed; no access hash is involved on either side.
    ///
    /// Do NOT use `messages.getDialogs` for hydration: it is users-only and
    /// bots get `400 BOT_METHOD_INVALID`.
    let buildGetChatsRequest (chatIds: int64 list) : TL.Methods.Messages_GetChats =
        let req = TL.Methods.Messages_GetChats()
        req.id <- Array.ofList chatIds
        req

    /// Caches basic-group peers from a `messages.chats` response. A basic group
    /// resolves to `inputPeerChat` (no access hash); the full `Chat` constructor
    /// mainly confirms the bot still knows the group.
    let cacheBasicChats
        (cache: PeerCache)
        (chats: System.Collections.Generic.KeyValuePair<int64, TL.ChatBase> seq)
        : unit =
        for kv in chats do
            cache.CacheChat(kv.Key)

/// The Telegram transport boundary, fakeable in tests.
type ITelegramTransport =
    abstract Login: unit -> Task<BotInfo>
    abstract SendMessage: SendTarget -> TaskResult<SendResult, SendError>
    abstract EditMessage: ChatId -> int64 -> string -> TelegramEntity list -> Task<unit>
    abstract DownloadVoice: VoiceRef -> Task<byte[]>
    abstract DownloadPhoto: PhotoRef -> Task<byte[]>
    abstract SetReaction: ChatId -> int64 -> string -> Task<unit>
    abstract SetTyping: ChatId -> Task<unit>
    abstract GetMessageSummary: ChatId -> int64 -> Task<MessageSummary option>
    /// Downloads the photo bytes of one message by id; None when the message is
    /// missing or carries no photo media. Used for reply-to-photo context.
    abstract DownloadMessagePhoto: ChatId -> int64 -> Task<byte[] option>
    abstract GetHistory: ChatId -> beforeId: int64 -> limit: int -> Task<HistoryEntry list>
