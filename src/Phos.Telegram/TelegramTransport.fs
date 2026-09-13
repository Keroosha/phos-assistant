namespace Phos.Telegram

open System
open System.IO
open System.Threading.Tasks
open Phos.Core.DomainTypes

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

    let client: WTelegram.Client =
        // `configProvider` returns `string | null`; a null result means "not
        // configured" to WTelegramClient. phone/code/2FA keys are never answered,
        // so null must cross the interop boundary untouched.
        new WTelegram.Client(
            Func<string, string>(fun key ->
                match Transport.configProvider config key with
                | null -> Unchecked.defaultof<string>
                | s -> s),
            // No in-memory session key: the session file is located via the
            // `session_pathname` config key, and no save callback is needed.
            Unchecked.defaultof<byte[]>,
            Unchecked.defaultof<Action<byte[]>>
        )

    let loginCore () : Task<BotInfo> =
        task {
            Transport.ensureSessionDir config.SessionPath
            let! botUser = client.LoginBotIfNeeded(config.BotToken)
            Transport.secureSessionFile config.SessionPath
            return Transport.extractBotInfo client.UserId botUser
        }

    let sendMessageCore (target: SendTarget) : Task<SendResult> =
        task {
            let peer = Transport.resolvePeer peers target.ChatId
            let req = Transport.buildSendRequest peer target
            let! result = client.Invoke(req)
            return { RemoteMessageId = Transport.extractRemoteMessageId result }
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
