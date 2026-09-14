namespace Phos.Telegram

open System
open System.IO
open System.Threading.Tasks
open Phos.Core.DomainTypes
open FsToolkit.ErrorHandling

/// WTelegramClient-backed transport. Wraps the client, wires the config
/// callback, secures the session directory, and resolves peers via `PeerCache`.
///
/// This is a thin interoperability wrapper over the WTelegramClient library:
/// its methods delegate directly to a live client connection and cannot be
/// exercised without real Telegram network access. All testable request
/// building, entity mapping, peer caching and response extraction live in the
/// `Transport` module (Transport.fs); this file is excluded from coverage per
/// the phase plan's interoperability-code exemption.
type TelegramTransport(config: TelegramConfig) =
    let peers = PeerCache()

    // SessionStore (a FileStream) opens the session file at Client construction,
    // so the session directory must already exist.
    do Transport.ensureSessionDir config.SessionPath

    let client: WTelegram.Client =
        // File-based session via `session_pathname` (SessionStore). The 3-arg
        // ctor (byte[]/Action<byte[]>) would wrap an ActionStore with a null
        // save callback → NRE on Session.Save() → session never persisted →
        // re-auth on every start → 420 FLOOD_WAIT.
        //
        // `configProvider` returns `string | null`; a null result means "not
        // configured" to WTelegramClient. phone/code/2FA keys are never answered,
        // so null must cross the interop boundary untouched.
        new WTelegram.Client(
            Func<string, string>(fun key ->
                match Transport.configProvider config key with
                | null -> Unchecked.defaultof<string>
                | s -> s)
        )

    let loginCore () : Task<BotInfo> =
        task {
            Transport.ensureSessionDir config.SessionPath
            let! botUser = client.LoginBotIfNeeded(config.BotToken)
            Transport.secureSessionFile config.SessionPath
            return Transport.extractBotInfo client.UserId botUser
        }

    let sendMessageCore (target: SendTarget) : TaskResult<SendResult, SendError> =
        taskResult {
            let peer = Transport.resolvePeer peers target.ChatId
            let req = Transport.buildSendRequest peer target

            try
                let! result = client.Invoke(req)
                return { RemoteMessageId = Transport.extractRemoteMessageId result }
            with ex ->
                return! Error(Transport.mapRpcError ex)
        }

    let editMessageCore (chat: ChatId) (messageId: int64) (text: string) (entities: TelegramEntity list) : Task<unit> =
        task {
            let peer = Transport.resolvePeer peers chat
            let req = Transport.buildEditRequest peer messageId text entities
            let! _ = client.Invoke(req)
            return ()
        }

    let downloadVoiceCore (voice: VoiceRef) : Task<byte[]> =
        task {
            let peer = Transport.resolvePeer peers voice.ChatId

            let inputMessage = TL.InputMessageID()
            inputMessage.id <- int voice.MessageId

            let! messages = client.GetMessages(peer, [| inputMessage :> TL.InputMessage |])

            let message =
                messages.Messages |> Array.tryFind (fun m -> m.ID = int voice.MessageId)

            match Transport.tryVoiceDocument message with
            | Some doc ->
                use stream = new MemoryStream()
                let! _ = client.DownloadFileAsync(doc, stream)
                return stream.ToArray()
            | None -> return Array.empty
        }

    let downloadPhotoCore (photo: PhotoRef) : Task<byte[]> =
        task {
            // Pick the largest size by area; `PhotoSizeBase` exposes Width/Height.
            let size = photo.Photo.sizes |> Array.maxBy (fun s -> s.Width * s.Height)

            use stream = new MemoryStream()
            let! _ = client.DownloadFileAsync(photo.Photo, stream, size, null)
            return stream.ToArray()
        }

    let downloadMessagePhotoCore (chat: ChatId) (messageId: int64) : Task<byte[] option> =
        task {
            let peer = Transport.resolvePeer peers chat

            let inputMessage = TL.InputMessageID()
            inputMessage.id <- int messageId

            let! messages = client.GetMessages(peer, [| inputMessage :> TL.InputMessage |])

            let photoRef =
                messages.Messages
                |> Array.tryFind (fun m -> m.ID = int messageId)
                |> Option.bind (fun m ->
                    match m with
                    | :? TL.Message as msg ->
                        match msg.media with
                        | :? TL.MessageMediaPhoto as mp ->
                            match mp.photo with
                            | :? TL.Photo as p ->
                                Some
                                    { ChatId = chat
                                      MessageId = messageId
                                      Photo = p }
                            | _ -> None
                        | _ -> None
                    | _ -> None)

            match photoRef with
            | Some ref ->
                let! b = downloadPhotoCore ref
                return Some b
            | None -> return None
        }

    let setReactionCore (chat: ChatId) (messageId: int64) (emoji: string) : Task<unit> =
        task {
            let peer = Transport.resolvePeer peers chat
            let req = Transport.buildReactionRequest peer messageId emoji
            let! _ = client.Invoke(req)
            return ()
        }

    let typingCore (chat: ChatId) : Task<unit> =
        task {
            let peer = Transport.resolvePeer peers chat
            let req = Transport.buildTypingRequest peer
            let! _ = client.Invoke(req)
            return ()
        }

    let getMessageSummaryCore (chat: ChatId) (messageId: int64) : Task<MessageSummary option> =
        task {
            let peer = Transport.resolvePeer peers chat

            let inputMessage = TL.InputMessageID()
            inputMessage.id <- int messageId

            let! messages = client.GetMessages(peer, [| inputMessage :> TL.InputMessage |])

            return
                messages.Messages
                |> Array.tryFind (fun m -> m.ID = int messageId)
                |> Option.map Transport.classifyMessage
        }

    let getHistoryCore (chat: ChatId) (beforeId: int64) (limit: int) : Task<HistoryEntry list> =
        task {
            let peer = Transport.resolvePeer peers chat
            let req = Transport.buildGetHistoryRequest peer beforeId limit
            let! result = client.Invoke(req)

            return
                match result with
                | :? TL.Messages_Messages as mm ->
                    mm.messages
                    |> Array.map (fun m ->
                        let fromBot =
                            match m with
                            | :? TL.Message as msg ->
                                match msg.from_id with
                                | :? TL.PeerUser as pu -> pu.user_id = client.UserId
                                | _ -> false
                            | _ -> false

                        { Id = int64 m.ID
                          FromBot = fromBot
                          Date = DateTimeOffset m.Date
                          Summary = Transport.classifyMessage m })
                    |> List.ofArray
                    |> List.rev
                | _ -> []
        }

    member _.OnUpdate(handler: TL.UpdatesBase -> Task<unit>) : unit =
        client.add_OnUpdates (fun (updates: TL.UpdatesBase) ->
            task {
                Transport.cachePeers peers updates
                do! handler updates
            })

    interface ITelegramTransport with
        member _.Login() = loginCore ()
        member _.SendMessage(target) = sendMessageCore target

        member _.EditMessage (chat) (messageId) (text) (entities) =
            editMessageCore chat messageId text entities

        member _.DownloadVoice(voice) = downloadVoiceCore voice
        member _.DownloadPhoto(photo) = downloadPhotoCore photo

        member _.SetReaction (chat) (messageId) (emoji) = setReactionCore chat messageId emoji

        member _.SetTyping(chat) = typingCore chat

        member _.GetMessageSummary (chat) (messageId) = getMessageSummaryCore chat messageId

        member _.DownloadMessagePhoto (chat) (messageId) = downloadMessagePhotoCore chat messageId

        member _.GetHistory (chat) (beforeId) (limit) = getHistoryCore chat beforeId limit
