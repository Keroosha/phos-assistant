namespace Phos.Omp

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open System.Text.Json.Nodes
open Microsoft.Extensions.Logging
open Phos.Core.DomainTypes
open Phos.Storage
open Phos.Telegram

/// Runtime configuration for the per-user session manager.
type SessionManagerOptions =
    { Profile: string
      SourceProfile: string
      IdleTimeout: TimeSpan
      MaxQueuePerUser: int }

/// Session-manager boundary consumed by the worker, so the worker is testable
/// with a fake (and the real `SessionManager` implements it).
type IOmpSessionManager =
    abstract EnsureRuntime: UserId -> Task<Result<unit, string>>
    abstract Prompt: UserId * Command -> Task<Result<unit, string>>
    abstract Abort: UserId -> Task<unit>
    abstract HandleEvent: UserId * JsonObject -> Task<unit>
    abstract IsRuntimeAlive: UserId -> bool
    abstract IdleTimeoutCheck: DateTimeOffset -> unit
    abstract Shutdown: unit -> unit

/// Mutable per-user runtime state. One RPC process + one in-memory command queue
/// per user; the durable `command_inbox` remains the source of truth.
type UserRuntime =
    { UserId: UserId
      Workspace: string
      mutable Process: IOmpProcess option
      mutable Client: IOmpRpcClient option
      mutable SessionId: string option
      mutable SessionFile: string option
      mutable LastActivity: DateTimeOffset
      mutable Busy: bool
      mutable Queue: ResizeArray<Command>
      mutable CurrentCommand: Command option
      /// RPC request id of the prompt currently in flight, so a legal late
      /// same-id failure response (immediate acceptance, later scheduling
      /// error) can be attributed to this turn.
      mutable PendingPromptId: string option
      mutable StreamState: StreamState
      mutable EventLock: SemaphoreSlim }

/// Owns the per-user OMP runtime: spawn/respawn with `--resume`, per-user
/// command queueing with a cap, event dispatch (host tools, host URIs, streamed
/// text), idle-timeout teardown and shutdown. The `turnEnded` callback lets the
/// worker finalize a command durably when a terminal `agent_end` arrives; its
/// `TurnOutcome` separates normal completion (and abort) from a provider/model
/// failure after OMP's own retry cycle, which must NOT be auto-retried.
type SessionManager
    (
        options: SessionManagerOptions,
        profile: ProfileManager,
        workspaces: WorkspaceManager,
        omp: OmpProcessOptions,
        hostTools: HostToolExecutor,
        hostUris: HostUriResolver,
        enqueueOutbox: OutboxEnvelope -> Task<unit>,
        turnEnded: int64 -> TurnOutcome -> Task<unit>,
        logger: ILogger,
        ?spawnProcess: OmpProcessOptions -> Result<IOmpProcess, string>,
        ?createClient: Process -> JsonObject -> IOmpRpcClient,
        ?now: unit -> DateTimeOffset
    ) as this =

    let now = defaultArg now (fun () -> DateTimeOffset.UtcNow)

    let spawn =
        defaultArg spawnProcess (fun o -> OmpProcess.Start(o, logger) |> Result.map (fun p -> p :> IOmpProcess))

    let createClient =
        defaultArg createClient (fun p rf -> OmpRpcClient(p, logger, readyFrame = rf) :> IOmpRpcClient)

    let runtimes = ConcurrentDictionary<UserId, UserRuntime>()

    let userInt (UserId id) = id

    let getRuntime (userId: UserId) : UserRuntime option =
        match runtimes.TryGetValue userId with
        | true, rt -> Some rt
        | _ -> None

    let getOrCreateRuntime (userId: UserId) (workspaceDir: string) : UserRuntime =
        match runtimes.TryGetValue userId with
        | true, rt -> rt
        | _ ->
            let rt =
                { UserId = userId
                  Workspace = workspaceDir
                  Process = None
                  Client = None
                  SessionId = None
                  SessionFile = None
                  LastActivity = now ()
                  Busy = false
                  Queue = ResizeArray()
                  CurrentCommand = None
                  PendingPromptId = None
                  StreamState = EventFormatter.initialState
                  EventLock = new SemaphoreSlim(1, 1) }

            runtimes[userId] <- rt
            rt

    /// RPC request id for a prompt of the given command. Stable per command so
    /// a late same-id failure response can be attributed.
    let promptIdOf (command: Command) : string = sprintf "prompt_%d" command.Id

    /// Prompts the current command on the user's client, marking the runtime as
    /// busy. A fresh dequeued command is prompted while the session is idle, so
    /// no `streamingBehavior` is required. The RPC request id is remembered so a
    /// legal late same-id failure response can be attributed to this turn.
    /// An immediate synchronous rejection is surfaced to the caller (the
    /// worker's prompt-error path) and puts the runtime back to idle; it is
    /// never swallowed here.
    let promptCommand (rt: UserRuntime) (command: Command) : Task<Result<unit, string>> =
        task {
            match rt.Client with
            | Some client ->
                rt.CurrentCommand <- Some command
                rt.Busy <- true
                rt.PendingPromptId <- Some(promptIdOf command)
                let message = command.Envelope.Payload
                let! result = client.PromptAsync(message, id = promptIdOf command, images = command.Envelope.Images)

                match result with
                | Ok _ -> return Ok()
                | Error e ->
                    logger.LogWarning("omp prompt rejected for command {Id}: {Error}", command.Id, e.Message)
                    rt.Busy <- false
                    rt.CurrentCommand <- None
                    rt.PendingPromptId <- None
                    rt.StreamState <- EventFormatter.initialState
                    return Error e.Message
            | None -> return Ok()
        }

    /// Finalizes the in-flight turn: releases the runtime, reports the outcome
    /// through the durable finalization callback and drains the next queued
    /// command. A provider failure is NOT requeued or re-prompted here — the
    /// OMP process/session stays alive for a future prompt. A queued command
    /// whose prompt is rejected immediately is finalized as a provider failure
    /// (no silent retry) instead of blocking the queue forever.
    let rec finalizeTurn (rt: UserRuntime) (command: Command) (outcome: TurnOutcome) : Task<unit> =
        task {
            rt.Busy <- false
            rt.CurrentCommand <- None
            rt.PendingPromptId <- None
            rt.StreamState <- EventFormatter.initialState

            do! turnEnded command.Id outcome

            if rt.Queue.Count > 0 then
                let next = rt.Queue.[0]
                rt.Queue.RemoveAt(0)
                let! prompted = promptCommand rt next

                match prompted with
                | Ok() -> ()
                | Error e -> do! finalizeTurn rt next (TurnOutcome.ProviderFailure e)
        }

    /// Marks the runtime dead and clears in-memory state. If a turn was in
    /// flight, its outcome is unknown (host tools may have already run), so it
    /// is parked as `needs_review` via the durable finalization callback —
    /// never replayed automatically.
    let onProcessExit (rt: UserRuntime) : unit =
        rt.Process <- None
        rt.Client <- None
        rt.Busy <- false
        let orphaned = rt.CurrentCommand
        rt.CurrentCommand <- None
        rt.PendingPromptId <- None
        rt.Queue.Clear()
        rt.StreamState <- EventFormatter.initialState
        logger.LogWarning("omp process exited for user {UserId}", userInt rt.UserId)

        match orphaned with
        | Some cmd ->
            let finalize = turnEnded cmd.Id TurnOutcome.NeedsReview
            finalize.ContinueWith(
                (fun (t: Task) ->
                    if t.IsFaulted then
                        logger.LogError(t.Exception, "needs_review finalization failed for command {Id}", cmd.Id)),
                TaskContinuationOptions.OnlyOnFaulted
            )
            |> ignore
        | None -> ()

    /// Handles a late RPC failure response for an already-accepted prompt: the
    /// runtime acknowledged `prompt` immediately, then emitted a failure with
    /// the same id (async prompt scheduling failed). Attributed via the
    /// remembered prompt id; the in-flight command is finalized as a provider
    /// failure — no duplicate prompt, no auto-retry. Failures that do not match
    /// the in-flight prompt id are ignored.
    let handleLatePromptFailure (rt: UserRuntime) (id: string) (err: RpcError) : Task<unit> =
        task {
            do! rt.EventLock.WaitAsync()

            try
                match rt.PendingPromptId, rt.CurrentCommand with
                | Some pendingId, Some cmd when pendingId = id ->
                    do! finalizeTurn rt cmd (TurnOutcome.ProviderFailure err.Message)
                | _ -> ()
            finally
                rt.EventLock.Release() |> ignore
        }

    /// Dispatches one inbound frame for a user. Serialized per runtime so events
    /// are processed in arrival order. Host tool/URI requests are executed and
    /// answered; session events are formatted into outbox envelopes; a terminal
    /// `agent_end` finalizes the turn via `turnEnded` and drains the next
    /// queued command. Retry boundaries (`auto_retry_start`, non-terminal
    /// `agent_end`) only refresh state and never finalize or notify.
    let handleEvent (rt: UserRuntime) (frame: JsonObject) : Task<unit> =
        task {
            rt.LastActivity <- now ()
            do! rt.EventLock.WaitAsync()

            try
                match RpcProtocol.classify frame with
                | FrameKind.HostToolCall ->
                    let! result =
                        hostTools.TryExecute(
                            rt.UserId,
                            rt.CurrentCommand |> Option.map (fun c -> c.Envelope.ChatId),
                            rt.CurrentCommand |> Option.map (fun c -> c.Envelope.Origin),
                            frame
                        )

                    match result with
                    | Some resFrame ->
                        match rt.Client with
                        | Some c -> do! c.SendRawAsync resFrame
                        | None -> ()
                    | None -> ()
                | FrameKind.HostUriRequest ->
                    let! result = hostUris.TryResolve frame

                    match result with
                    | Some resFrame ->
                        match rt.Client with
                        | Some c -> do! c.SendRawAsync resFrame
                        | None -> ()
                    | None -> ()
                | _ ->
                    let ctx =
                        match rt.CurrentCommand with
                        | Some cmd ->
                            { CommandId = cmd.Id
                              ChatId = cmd.Envelope.ChatId }
                        | None -> { CommandId = 0L; ChatId = ChatId 0L }

                    let st, envelopes, outcome = EventFormatter.onEvent ctx rt.StreamState frame
                    rt.StreamState <- st

                    for e in envelopes do
                        do! enqueueOutbox e

                    match Json.getString "type" frame with
                    | Some "agent_start" -> rt.Busy <- true
                    | Some "agent_end" ->
                        // `outcome` is `Some` only for a terminal `agent_end`.
                        // A non-terminal `agent_end` means the session will
                        // resume (maintenance/async delivery — including
                        // OMP's auto-retry cycle): the turn is NOT over, the
                        // runtime stays busy and nothing is finalized or
                        // notified.
                        match outcome with
                        | Some turnOutcome ->
                            let finished = rt.CurrentCommand

                            match finished with
                            | Some cmd -> do! finalizeTurn rt cmd turnOutcome
                            | None ->
                                rt.Busy <- false
                                rt.PendingPromptId <- None
                                rt.StreamState <- EventFormatter.initialState
                        | None -> ()
                    | _ -> ()
            finally
                rt.EventLock.Release() |> ignore
        }

    /// Spawns the OMP process (resuming the user's session when known), wires
    /// the client and event handlers, captures the session id/file, and
    /// registers the host tools and `tg://` URI scheme.
    let spawnRuntime (rt: UserRuntime) : Task<Result<unit, string>> =
        task {
            let resume = rt.SessionId

            let ompOptions =
                { omp with
                    WorkspaceDir = rt.Workspace
                    SessionResume = resume }

            match spawn ompOptions with
            | Error e -> return Error e
            | Ok proc ->
                let client = createClient proc.Process proc.ReadyFrame
                rt.Process <- Some proc
                rt.Client <- Some client

                client.EventReceived.Add(fun frame ->
                    let t = handleEvent rt frame

                    t.ContinueWith(
                        (fun (t: Task) ->
                            if t.IsFaulted then
                                logger.LogError(
                                    t.Exception,
                                    "omp event handling failed for user {UserId}",
                                    userInt rt.UserId
                                )),
                        TaskContinuationOptions.OnlyOnFaulted
                    )
                    |> ignore)

                client.LateFailure.Add(fun (id, err) ->
                    let t = handleLatePromptFailure rt id err

                    t.ContinueWith(
                        (fun (t: Task) ->
                            if t.IsFaulted then
                                logger.LogError(
                                    t.Exception,
                                    "omp late prompt failure handling failed for user {UserId}",
                                    userInt rt.UserId
                                )),
                        TaskContinuationOptions.OnlyOnFaulted
                    )
                    |> ignore)

                client.ParseError.Add(fun e -> logger.LogWarning("omp parse error: {Error}", e))

                proc.Exited.Add(fun () -> onProcessExit rt)

                match! client.GetStateAsync() with
                | Ok state ->
                    match state with
                    | :? JsonObject as so ->
                        rt.SessionId <- Json.getString "sessionId" so
                        rt.SessionFile <- Json.getString "sessionFile" so
                    | _ -> ()
                | Error e -> logger.LogWarning("omp get_state failed: {Error}", e.Message)

                match! client.SetHostToolsAsync HostTools.definitions with
                | Ok _ -> ()
                | Error e -> logger.LogWarning("omp set_host_tools failed: {Error}", e.Message)

                match!
                    client.SetHostUriSchemesAsync
                        [ { Scheme = "tg"
                            Description = "Telegram voice messages and chat history"
                            Writable = false
                            Immutable = false } ]
                with
                | Ok _ -> ()
                | Error e -> logger.LogWarning("omp set_host_uri_schemes failed: {Error}", e.Message)

                rt.LastActivity <- now ()
                return Ok()
        }

    /// Ensures the profile, workspace and a live RPC process exist for the user.
    member _.EnsureRuntime(userId: UserId) : Task<Result<unit, string>> =
        task {
            match profile.EnsureProfile(options.Profile, options.SourceProfile) with
            | Error e -> return Error e
            | Ok() ->
                match workspaces.Ensure userId with
                | Error e -> return Error e
                | Ok wsDir ->
                    let rt = getOrCreateRuntime userId wsDir

                    match rt.Process with
                    | Some _ -> return Ok()
                    | None -> return! spawnRuntime rt
        }

    /// Accepts a command for the user: enqueues it when the session is busy or
    /// the queue is non-empty (bounded by `MaxQueuePerUser`), otherwise prompts
    /// it immediately. A full queue returns `Error "queue full"` and leaves the
    /// command durable in the inbox (no loss).
    member _.Prompt(userId: UserId, command: Command) : Task<Result<unit, string>> =
        task {
            match! this.EnsureRuntime userId with
            | Error e -> return Error e
            | Ok() ->
                let rt = runtimes[userId]

                if rt.Busy || rt.Queue.Count > 0 then
                    if rt.Queue.Count >= options.MaxQueuePerUser then
                        return Error "queue full"
                    else
                        rt.Queue.Add command
                        rt.LastActivity <- now ()
                        return Ok()
                else
                    let! prompted = promptCommand rt command
                    rt.LastActivity <- now ()
                    return prompted
        }

    /// Aborts the current turn (`/stop`).
    member _.Abort(userId: UserId) : Task<unit> =
        task {
            match getRuntime userId with
            | Some rt ->
                match rt.Client with
                | Some c ->
                    let! _ = c.AbortAsync()
                    ()
                | None -> ()
            | None -> ()
        }

    /// Feeds a raw inbound frame into the runtime's event pipeline.
    member _.HandleEvent(userId: UserId, frame: JsonObject) : Task<unit> =
        task {
            match getRuntime userId with
            | Some rt -> do! handleEvent rt frame
            | None -> ()
        }

    /// True when the user has a live, non-exited OMP process.
    member _.IsRuntimeAlive(userId: UserId) : bool =
        match getRuntime userId with
        | Some rt ->
            match rt.Process with
            | Some p -> not p.IsDead
            | None -> false
        | None -> false

    /// Kills and clears runtimes idle longer than `IdleTimeout`. The next
    /// command respawns on demand with `--resume`. A runtime that saw no
    /// events for the whole idle window while a turn was in flight is
    /// wedged/dead: its in-flight command has an unknown outcome and is parked
    /// as `needs_review` (never replayed automatically) — the idle timeout is
    /// the generous-grace emergency path, not a turn deadline.
    member _.IdleTimeoutCheck(at: DateTimeOffset) : unit =
        for kv in runtimes do
            let rt = kv.Value

            if at - rt.LastActivity > options.IdleTimeout then
                rt.Client
                |> Option.iter (fun c ->
                    try
                        c.Dispose()
                    with _ ->
                        ())

                rt.Process
                |> Option.iter (fun p ->
                    try
                        p.Kill()
                    with _ ->
                        ())

                rt.Process <- None
                rt.Client <- None
                rt.Busy <- false
                let orphaned = rt.CurrentCommand
                rt.CurrentCommand <- None
                rt.PendingPromptId <- None
                rt.Queue.Clear()
                rt.StreamState <- EventFormatter.initialState

                match orphaned with
                | Some cmd ->
                    let finalize = turnEnded cmd.Id TurnOutcome.NeedsReview
                    finalize.ContinueWith(
                            (fun (t: Task) ->
                                if t.IsFaulted then
                                    logger.LogError(
                                        t.Exception,
                                        "needs_review finalization failed for command {Id}",
                                        cmd.Id
                                    )),
                            TaskContinuationOptions.OnlyOnFaulted
                        )
                        |> ignore
                | None -> ()

    /// Tears down every runtime (abort busy turns and kill processes).
    member _.Shutdown() : unit =
        for kv in runtimes do
            let rt = kv.Value

            rt.Client
            |> Option.iter (fun c ->
                try
                    c.Dispose()
                with _ ->
                    ())

            rt.Process
            |> Option.iter (fun p ->
                try
                    p.Kill()
                with _ ->
                    ())

    interface IOmpSessionManager with
        member _.EnsureRuntime(userId: UserId) = this.EnsureRuntime userId
        member _.Prompt(userId: UserId, command: Command) = this.Prompt(userId, command)
        member _.Abort(userId: UserId) = this.Abort userId
        member _.HandleEvent(userId: UserId, frame: JsonObject) = this.HandleEvent(userId, frame)
        member _.IsRuntimeAlive(userId: UserId) = this.IsRuntimeAlive userId
        member _.IdleTimeoutCheck(at: DateTimeOffset) = this.IdleTimeoutCheck at
        member _.Shutdown() = this.Shutdown()
