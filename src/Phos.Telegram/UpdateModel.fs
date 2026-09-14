namespace Phos.Telegram

open System
open Phos.Core.DomainTypes
open Phos.Core.Chunker

/// A normalized Telegram update that the handler can process without touching
/// the WTelegramClient types.
type IncomingUpdate =
    { UpdateId: int64
      Chat: Chat
      From: User
      Text: string option
      Voice: VoiceRef option
      MessageId: int64
      Photo: PhotoRef option
      IsSticker: bool
      ReplyToMessageId: int64 option }

/// Mappers from WTelegramClient update/message objects to `IncomingUpdate`.
module UpdateModel =

    /// Maps a `TL.Peer` to the domain chat (kind + id).
    let private peerToChat (peer: TL.Peer) : Chat option =
        match peer with
        | :? TL.PeerUser as p ->
            Some
                { Id = ChatId p.user_id
                  Kind = Private }
        | :? TL.PeerChat as p -> Some { Id = ChatId p.chat_id; Kind = Group }
        | :? TL.PeerChannel as p ->
            Some
                { Id = ChatId p.channel_id
                  Kind = Channel }
        | _ -> None

    /// Extracts the sender user id from a `TL.Peer`.
    let private peerToUserId (peer: TL.Peer) : UserId option =
        match peer with
        | :? TL.PeerUser as p -> Some(UserId p.user_id)
        | _ -> None

    /// Builds a stable per-(chat, message) update id. Telegram message ids are
    /// 32-bit, so combining with the chat id keeps the value unique across chats.
    let private mkUpdateId (ChatId cid) (messageId: int64) : int64 = (int64 messageId) ^^^ (cid <<< 32)

    /// True when the document is a voice message (OGG/Opus voice note).
    let private isVoiceDocument (doc: TL.Document) : bool =
        let mt = if isNull doc.mime_type then "" else doc.mime_type
        mt.StartsWith "audio/ogg" || mt.StartsWith "audio/opus"

    /// Extracts a `VoiceRef` from a voice message, if any.
    let private tryVoice (m: TL.Message) (chat: Chat) : VoiceRef option =
        match m.media with
        | :? TL.MessageMediaDocument as media ->
            match media.document with
            | :? TL.Document as doc when isVoiceDocument doc ->
                Some
                    { ChatId = chat.Id
                      MessageId = int64 m.id
                      FileReference = doc.file_reference
                      AccessHash = doc.access_hash }
            | _ -> None
        | _ -> None

    /// Extracts a `PhotoRef` from a photo message, if any.
    let private tryPhoto (m: TL.Message) (chat: Chat) : PhotoRef option =
        match m.media with
        | :? TL.MessageMediaPhoto as media ->
            match media.photo with
            | :? TL.Photo as p ->
                Some
                    { ChatId = chat.Id
                      MessageId = int64 m.id
                      Photo = p }
            | _ -> None
        | _ -> None

    /// True when the document is a sticker: either it carries a
    /// `DocumentAttributeSticker`, or its mime type is `image/webp`.
    let private isStickerDocument (doc: TL.Document) : bool =
        let hasStickerAttribute =
            not (isNull doc.attributes)
            && doc.attributes |> Array.exists (fun a -> a :? TL.DocumentAttributeSticker)

        let mime = if isNull doc.mime_type then "" else doc.mime_type
        hasStickerAttribute || mime.StartsWith "image/webp"

    /// True when the message is a sticker (a document with a sticker attribute).
    let private isStickerMessage (m: TL.Message) : bool =
        match m.media with
        | :? TL.MessageMediaDocument as media ->
            match media.document with
            | :? TL.Document as doc -> isStickerDocument doc
            | _ -> false
        | _ -> false

    /// Maps a `TL.MessageBase` to an `IncomingUpdate`, if it is a user message
    /// the bot can act on. Service messages and channel posts are ignored.
    let tryMapMessage (message: TL.MessageBase) : IncomingUpdate option =
        match message with
        | :? TL.Message as m when not (isNull m.peer_id) ->
            let chat = peerToChat m.peer_id

            // Telegram omits `from_id` for private-chat messages: the `peer_id`
            // IS the sender. Groups/channels keep `from_id` when a user sends.
            let fromId =
                match peerToUserId m.from_id with
                | Some uid -> Some uid
                | None ->
                    match chat with
                    | Some { Id = ChatId uid; Kind = Private } -> Some(UserId uid)
                    | _ -> None

            match chat, fromId with
            | Some chat, Some uid ->
                let voice = tryVoice m chat
                let photo = tryPhoto m chat
                let isSticker = isStickerMessage m

                let text =
                    if String.IsNullOrEmpty m.message then
                        None
                    else
                        Some m.message

                Some
                    { UpdateId = mkUpdateId chat.Id (int64 m.id)
                      Chat = chat
                      From =
                        { Id = uid
                          Username = None
                          Role = User }
                      Text = text
                      Voice = voice
                      MessageId = int64 m.id
                      Photo = photo
                      IsSticker = isSticker
                      ReplyToMessageId =
                        match m.reply_to with
                        | :? TL.MessageReplyHeader as h -> Some(int64 h.reply_to_msg_id)
                        | _ -> None }
            | _ -> None
        | _ -> None

    /// Maps an `UpdateNewMessage` update to an `IncomingUpdate`.
    let tryMapUpdateNewMessage (update: TL.UpdateNewMessage) : IncomingUpdate option = tryMapMessage update.message

    /// Maps a whole `UpdatesBase` batch to the list of actionable updates.
    let mapUpdates (updates: TL.UpdatesBase) : IncomingUpdate list =
        updates.UpdateList
        |> Array.choose (fun u ->
            match u with
            | :? TL.UpdateNewMessage as un -> tryMapMessage un.message
            | _ -> None)
        |> List.ofArray
