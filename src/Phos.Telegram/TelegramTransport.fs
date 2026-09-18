namespace Phos.Telegram

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
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
type TelegramTransport(config: TelegramConfig, ?logger: ILogger) =
    let logger = defaultArg logger NullLogger.Instance
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

    /// Login-readiness gate. Completed only after `LoginBotIfNeeded` succeeds
    /// (see `loginCore`); peer hydration awaits it so no API call is attempted
    /// before the client is connected and authorized.
    let loginReady = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let loginCore () : Task<BotInfo> =
        task {
            Transport.ensureSessionDir config.SessionPath
            let! botUser = client.LoginBotIfNeeded(config.BotToken)
            Transport.secureSessionFile config.SessionPath

            // The login-readiness gate completes ONLY here, after
            // `LoginBotIfNeeded` has succeeded — hydration (and any other
            // operation that needs the reactor) must never race the login loop.
            loginReady.TrySetResult() |> ignore

            return Transport.extractBotInfo client.UserId botUser
        }

    /// Serializes hydration attempts. Waiters entering after a completed pass
    /// recheck the cache under the semaphore and typically find the peer already
    /// hydrated by an earlier waiter, so concurrent missing-peer failures for
    /// the same chat coalesce into one refetch instead of a refetch storm.
    let hydrationGate = new SemaphoreSlim(1, 1)

    /// Per-chat cooldown/in-flight suppression after a failed hydration pass.
    /// Raw MTProto chat id -> earliest moment a new pass may probe again:
    /// during an outage concurrent/rapid misses must not repeat all three RPC
    /// probes. Cache hits bypass the gate entirely, so this never delays the
    /// steady-state path.
    let hydrationCooldown = ConcurrentDictionary<int64, DateTimeOffset>()

    /// Backoff applied to a chat after a hydration pass cannot resolve it.
    let hydrationCooldownSpan = TimeSpan.FromSeconds 30.0

    /// Refetches missing peers only after login readiness. Cache hits bypass the
    /// gate, concurrent passes are coalesced, and failed/ambiguous chats are
    /// cooled down. All failures are logged at Debug and never escape this
    /// best-effort recovery path.
    let hydratePeersCore (chatIds: ChatId list) : Task<bool> =
        task {
            if List.isEmpty chatIds then
                return false
            elif not loginReady.Task.IsCompleted then
                // Hydration never races the login loop: before login is ready,
                // leave the peer unresolved and let callers retry later.
                return false
            else
                let! _ = hydrationGate.WaitAsync()

                try
                    // Recheck under the gate: an earlier waiter may have
                    // already hydrated (part of) these peers, and a chat whose
                    // previous pass failed is cooling down.
                    let nowUtc = DateTimeOffset.UtcNow

                    let isCoolingDown (cid: int64) =
                        match hydrationCooldown.TryGetValue cid with
                        | true, until -> until > nowUtc
                        | _ -> false

                    let missing =
                        chatIds
                        |> List.filter (fun (ChatId cid) ->
                            peers.Get (ChatId cid) |> Option.isNone
                            && not (isCoolingDown cid))

                    let attempted = not (List.isEmpty missing)

                    if attempted then
                        // Raw MTProto user/chat/channel id sequences OVERLAP
                        // (see core.telegram.org/api/bots/ids), so peer kind is
                        // NOT classified by sign: every id is probed against all
                        // three refetch methods. Each probe writes into its OWN
                        // candidate cache, so a raw-id collision can never
                        // overwrite the intended peer — only an unambiguous
                        // single candidate is merged into the real PeerCache.
                        let ids = missing |> List.map (fun (ChatId cid) -> cid)

                        let userCandidates =
                            ConcurrentDictionary<int64, TL.InputPeer>()

                        let channelCandidates =
                            ConcurrentDictionary<int64, TL.InputPeer>()

                        let chatCandidates =
                            ConcurrentDictionary<int64, TL.InputPeer>()

                        for batch in ids |> List.chunkBySize 100 do
                            // users.getUsers with zero access hashes — bots may
                            // address peers they have already interacted with.
                            // Wrong-kind errors are expected, so keep them Debug.
                            try
                                let! users = client.Invoke(Transport.buildGetUsersRequest batch)
                                Transport.collectUsers userCandidates users
                            with ex ->
                                logger.LogDebug(ex, "peer hydration probe (users.getUsers) rejected")

                            // channels.getChannels with zero access hashes.
                            try
                                let! chats = client.Invoke(Transport.buildGetChannelsRequest batch)
                                Transport.collectChannels channelCandidates chats.chats
                            with ex ->
                                logger.LogDebug(ex, "peer hydration probe (channels.getChannels) rejected")

                            // messages.getChats resolves legacy basic groups.
                            // messages.getDialogs is users-only and must not be used.
                            try
                                let! chats = client.Invoke(Transport.buildGetChatsRequest batch)
                                Transport.collectBasicChats chatCandidates chats.chats
                            with ex ->
                                logger.LogDebug(ex, "peer hydration probe (messages.getChats) rejected")

                        // Merge only an unambiguous candidate. Zero candidates
                        // and raw-id collisions enter cooldown.
                        let cooldownUntil = nowUtc.Add hydrationCooldownSpan

                        for cid in ids do
                            let candidates =
                                [ if userCandidates.ContainsKey cid then userCandidates.[cid]
                                  if channelCandidates.ContainsKey cid then channelCandidates.[cid]
                                  if chatCandidates.ContainsKey cid then chatCandidates.[cid] ]

                            match candidates with
                            | [ peer ] -> peers.Cache(ChatId cid, peer)
                            | _ -> hydrationCooldown.[cid] <- cooldownUntil

                    hydrationGate.Release() |> ignore
                    return attempted
                with ex ->
                    hydrationGate.Release() |> ignore
                    return raise ex
        }
    /// Runs `op` with the peer resolved from the cache. On a missing peer (the
    /// in-memory cache is empty after a restart while durable rows survive),
    /// hydrates the chat once and retries — the hydration gate coalesces
    /// concurrent retries for the same chat, and cache hits bypass the login
    /// gate entirely so the steady-state update path never blocks. If hydration
    /// cannot resolve the peer either, the `NoCachedPeerException` propagates to
    /// the caller (typed, retryable at the caller's discretion).
    let withPeer (chat: ChatId) (op: TL.InputPeer -> Task<'a>) : Task<'a> =
        task {
            try
                return! op (Transport.resolvePeer peers chat)
            with :? NoCachedPeerException ->
                let! _ = hydratePeersCore [ chat ]

                // Retry once: the peer is now cached, or hydration failed and
                // the typed error surfaces to the caller.
                return! op (Transport.resolvePeer peers chat)
        }

    /// Resolves a send destination and preserves whether hydration was
    /// attempted. Login/cooldown misses are deferred without consuming a
    /// delivery attempt; a completed but unsuccessful probe is bounded by the
    /// outbox retry policy.
    let resolveSendPeer (chat: ChatId) : Task<Result<TL.InputPeer, MissingPeerReason>> =
        task {
            match peers.Get chat with
            | Some peer -> return Ok peer
            | None ->
                let! probed = hydratePeersCore [ chat ]

                match peers.Get chat with
                | Some peer -> return Ok peer
                | None ->
                    return
                        Error(
                            if probed then
                                MissingPeerReason.HydrationFailed
                            else
                                MissingPeerReason.LoginNotReady
                        )
        }

    let sendMessageCore (target: SendTarget) : TaskResult<SendResult, SendError> =
        task {
            match! resolveSendPeer target.ChatId with
            | Error reason -> return Error(MissingPeer(target.ChatId, reason))
            | Ok peer ->
                try
                    let req = Transport.buildSendRequest peer target
                    let! result = client.Invoke(req)
                    return Ok { RemoteMessageId = Transport.extractRemoteMessageId result }
                with ex ->
                    return Error(Transport.mapRpcError ex)
        }

    let editMessageCore (chat: ChatId) (messageId: int64) (text: string) (entities: TelegramEntity list) : Task<unit> =
        withPeer chat (fun peer ->
            task {
                let req = Transport.buildEditRequest peer messageId text entities
                let! _ = client.Invoke(req)
                return ()
            })

    let downloadVoiceCore (voice: VoiceRef) : Task<byte[]> =
        withPeer voice.ChatId (fun peer ->
            task {
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
            })

    let downloadPhotoCore (photo: PhotoRef) : Task<byte[]> =
        task {
            // Pick the largest size by area; `PhotoSizeBase` exposes Width/Height.
            let size = photo.Photo.sizes |> Array.maxBy (fun s -> s.Width * s.Height)

            use stream = new MemoryStream()
            let! _ = client.DownloadFileAsync(photo.Photo, stream, size, null)
            return stream.ToArray()
        }

    let downloadMessagePhotoCore (chat: ChatId) (messageId: int64) : Task<byte[] option> =
        withPeer chat (fun peer ->
            task {
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
            })

    let setReactionCore (chat: ChatId) (messageId: int64) (emoji: string) : Task<unit> =
        withPeer chat (fun peer ->
            task {
                let req = Transport.buildReactionRequest peer messageId emoji
                let! _ = client.Invoke(req)
                return ()
            })

    let typingCore (chat: ChatId) : Task<unit> =
        withPeer chat (fun peer ->
            task {
                let req = Transport.buildTypingRequest peer
                let! _ = client.Invoke(req)
                return ()
            })

    let getMessageSummaryCore (chat: ChatId) (messageId: int64) : Task<MessageSummary option> =
        withPeer chat (fun peer ->
            task {
                let inputMessage = TL.InputMessageID()
                inputMessage.id <- int messageId

                let! messages = client.GetMessages(peer, [| inputMessage :> TL.InputMessage |])

                return
                    messages.Messages
                    |> Array.tryFind (fun m -> m.ID = int messageId)
                    |> Option.map Transport.classifyMessage
            })

    let getHistoryCore (chat: ChatId) (beforeId: int64) (limit: int) : Task<HistoryEntry list> =
        withPeer chat (fun peer ->
            task {
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
            })

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
