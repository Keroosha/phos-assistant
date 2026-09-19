namespace Phos.Omp

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Phos.Core.DomainTypes
open Phos.Storage
open Phos.Telegram

module Inbox = Phos.Core.InboxStateMachine

/// A coalescing wake signal. The bounded channel holds at most one pending wake;
/// a write when it is already full is dropped (a scan is already scheduled), so
/// the channel never blocks the Telegram admission path.
type WakeChannel() =
    let channel = Channel.CreateBounded<unit>(BoundedChannelOptions(1))

    member _.Wake() : unit = channel.Writer.TryWrite(()) |> ignore

    member _.WaitAsync(ct: CancellationToken) : Task<unit> =
        task {
            try
                let! available = channel.Reader.WaitToReadAsync(ct)

                if available then
                    let mutable item = ()
                    channel.Reader.TryRead(&item) |> ignore

                return ()
            with :? ChannelClosedException ->
                return ()
        }

/// Background service that drains the durable `command_inbox` into OMP sessions.
/// It owns the at-least-once path: expired leases are reclaimed, each pending
/// command is claimed with a lease, `/stop` aborts the session, prompts are
/// marked started with a heartbeat, and a terminal `agent_end` completes the
/// command via the `turnEnded` callback shared with `SessionManager`.
type OmpWorker
    (
        inbox: ICommandInbox,
        outbox: IMessageOutbox,
        sessions: IOmpSessionManager,
        wake: WakeChannel,
        heartbeats: ConcurrentDictionary<int64, CancellationTokenSource>,
        transport: ITelegramTransport,
        logger: ILogger<OmpWorker>
    ) =
    inherit BackgroundService()

    let now () = DateTimeOffset.UtcNow

    let isStop (payload: string) : bool = payload.StartsWith "/stop"

    let reply (cmd: Command) (text: string) : Task<unit> =
        task {
            let chunks = EntitySend.chunkForSend text []

            for i, chunk in List.indexed chunks do
                let! _ = outbox.Insert cmd.Id i cmd.Envelope.ChatId (Random.Shared.NextInt64()) chunk.Text []
                ()
        }

    /// Recovers the original Telegram message id from the admission key
    /// `tg:<updateId>` (see `UpdateModel.mkUpdateId`: updateId XORs the 32-bit
    /// message id with the chat id; XOR is its own inverse).
    let messageIdOf (cmd: Command) : int64 option =
        match cmd.Envelope.ExternalKey with
        | Some key when key.StartsWith "tg:" ->
            match Int64.TryParse(key.Substring 3) with
            | true, updateId ->
                let (ChatId cid) = cmd.Envelope.ChatId
                Some(updateId ^^^ (cid <<< 32))
            | _ -> None
        | _ -> None

    /// Acknowledges an accepted command with a 👀 reaction on the user's
    /// message (the owner's chosen ack signal instead of a streamed "…").
    let acknowledge (cmd: Command) : Task<unit> =
        task {
            match messageIdOf cmd with
            | Some messageId ->
                try
                    do! transport.SetReaction cmd.Envelope.ChatId messageId "👀"
                with ex ->
                    match ex with
                    | :? NoCachedPeerException ->
                        // Post-restart the peer cache is empty; the transport
                        // has already tried to hydrate once. A missing peer
                        // must not spam warnings (the command itself is
                        // durable), so log at Debug only.
                        logger.LogDebug(
                            "peer for command {Id} (chat {ChatId}) not resolvable yet; reaction skipped",
                            cmd.Id,
                            cmd.Envelope.ChatId
                        )
                    | _ -> logger.LogWarning(ex, "set reaction failed for command {Id}", cmd.Id)
            | None -> ()
        }

    /// Keeps the lease alive and the "bot is typing" bubble fresh while the
    /// command runs. Telegram expires the typing bubble after ~5s, so it is
    /// re-sent every 4s. Stops when the command is no longer running or the
    /// user's OMP process died (so the lease expires and the command is
    /// reclaimed — no loss, no duplicate execution).
    let startHeartbeat (cmdId: int64) (userId: UserId) (chatId: ChatId) : unit =
        let cts = new CancellationTokenSource()
        heartbeats.[cmdId] <- cts

        let setTyping () : Task<unit> =
            task {
                try
                    do! transport.SetTyping chatId
                with ex ->
                    match ex with
                    | :? NoCachedPeerException ->
                        // Post-restart the peer cache is empty; the transport
                        // has already tried to hydrate once. Retyping every 4s
                        // must not produce a warning storm while the peer is
                        // unresolved — log at Debug only.
                        logger.LogDebug("peer for command {Id} not resolvable yet; typing skipped", cmdId)
                    | _ -> logger.LogWarning(ex, "set typing failed for command {Id}", cmdId)
            }

        let loop: Task =
            task {
                try
                    // Send one typing immediately so the user sees feedback as
                    // soon as the command is accepted.
                    do! setTyping ()

                    let mutable running = true
                    let mutable tick = 0

                    while running && not cts.IsCancellationRequested do
                        do! Task.Delay(4000, cts.Token)
                        tick <- tick + 1

                        if not (sessions.IsRuntimeAlive userId) then
                            running <- false
                        else
                            let! cmd = inbox.GetById cmdId

                            match cmd with
                            | Some c when c.Status = Inbox.Status.Running ->
                                do! setTyping ()

                                // Heartbeat every 5th tick (20s) to keep the
                                // lease alive.
                                if tick % 5 = 0 then
                                    do! inbox.Heartbeat cmdId (now ()) (now().AddSeconds 60.0)
                            | _ -> running <- false
                with :? OperationCanceledException ->
                    ()
            }

        loop |> ignore

    let processCommand (cmd: Command) : Task<unit> =
        task {
            let user = cmd.Envelope.UserId

            logger.LogInformation(
                "command {Id} processing (chat {ChatId}, user {UserId})",
                cmd.Id,
                cmd.Envelope.ChatId,
                user
            )

            if isStop cmd.Envelope.Payload then
                logger.LogInformation("command {Id}: /stop handled", cmd.Id)
                do! acknowledge cmd
                do! sessions.Abort user
                do! inbox.MarkStarted cmd.Id
                do! inbox.MarkCompleted cmd.Id
                do! reply cmd "⏹ остановлено"
            else

                match! sessions.Prompt(user, cmd) with
                | Ok() ->
                    logger.LogInformation("command {Id} accepted", cmd.Id)
                    do! acknowledge cmd
                    do! inbox.MarkStarted cmd.Id
                    startHeartbeat cmd.Id user cmd.Envelope.ChatId
                | Error msg when msg = "queue full" ->
                    // Durable no-loss: the command stays `claimed`; its lease
                    // expires and `ExpireLeases` returns it to `pending`, so the
                    // next scan retries once the queue drains.
                    logger.LogDebug("command {Id} deferred: queue full", cmd.Id)
                | Error msg ->
                    logger.LogWarning("omp prompt failed for command {Id}: {Error}", cmd.Id, msg)
                    do! inbox.MarkFailed cmd.Id
                    let! c = inbox.GetById cmd.Id

                    match c with
                    | Some c when c.Status <> Inbox.Status.DeadLetter -> do! inbox.Retry cmd.Id
                    | _ -> ()
        }

    let processChat (chatId: ChatId) : Task<unit> =
        task {
            let lease =
                { Until = now().AddSeconds 60.0
                  HeartbeatAt = now () }

            let! cmd = inbox.ClaimNextForChat chatId lease

            match cmd with
            | Some cmd -> do! processCommand cmd
            | None -> ()
        }

    /// Test seam: runs the per-command processing logic directly (used by the
    /// worker unit tests without driving the whole hosted loop).
    member internal _.ProcessCommandForTest(cmd: Command) : Task<unit> = processCommand cmd

    override _.ExecuteAsync(stoppingToken: CancellationToken) : Task =
        task {
            while not stoppingToken.IsCancellationRequested do
                try
                    let! _ = inbox.ExpireLeases(now ())
                    let! chatIds = inbox.ListPendingChatIds()

                    for chatId in chatIds do
                        do! processChat chatId
                with ex ->
                    logger.LogError(ex, "omp worker scan failed")

                let wakeTask: Task = wake.WaitAsync(stoppingToken)
                let delayTask: Task = Task.Delay(5000, stoppingToken)
                let! _ = Task.WhenAny(wakeTask, delayTask)

                ()
        }
