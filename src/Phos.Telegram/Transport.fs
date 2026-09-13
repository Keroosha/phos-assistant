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
      Kind: EntityKind }

/// A message to send, with a stable MTProto `random_id`.
type SendTarget =
    { ChatId: ChatId
      RandomId: int64
      Text: string
      Entities: TelegramEntity list }

/// Result of sending a message.
type SendResult = { RemoteMessageId: int64 }

/// Expected Telegram-side send failures, typed so callers never parse exception text.
type SendError =
    | FloodWait of int
    | SlowModeWait of int
    | Other of string

/// Reference to a Telegram voice message to download.
type VoiceRef =
    { ChatId: ChatId
      MessageId: int64
      FileReference: byte[]
      AccessHash: int64 }

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
            | TextUrl -> TL.MessageEntityTextUrl() :> TL.MessageEntity
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

        if not (List.isEmpty entities) then
            req.entities <- entities |> List.map toTLMessageEntity |> List.toArray

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
    /// Recognizes `FLOOD_WAIT_<n>` / `SLOWMODE_WAIT_<n>` (underscore optional);
    /// everything else becomes `Other`.
    let mapRpcError (ex: exn) : SendError =
        let m = Regex.Match(ex.Message, "(FLOOD_WAIT|SLOWMODE_WAIT)_?(\d+)")

        if m.Success then
            let seconds = int m.Groups.[2].Value

            match m.Groups.[1].Value with
            | "FLOOD_WAIT" -> FloodWait seconds
            | _ -> SlowModeWait seconds
        else
            Other ex.Message

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

    /// Resolves a chat to its `InputPeer` from the cache.
    let resolvePeer (cache: PeerCache) (chat: ChatId) : TL.InputPeer =
        match cache.Get chat with
        | Some peer -> peer
        | None -> failwithf "no cached peer for %A" chat

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

/// The Telegram transport boundary, fakeable in tests.
type ITelegramTransport =
    abstract Login: unit -> Task<BotInfo>
    abstract SendMessage: SendTarget -> TaskResult<SendResult, SendError>
    abstract EditMessage: ChatId -> int64 -> string -> TelegramEntity list -> Task<unit>
    abstract DownloadVoice: VoiceRef -> Task<byte[]>
