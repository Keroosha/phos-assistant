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
      mutable StreamState: StreamState
      mutable EventLock: SemaphoreSlim }

/// Owns the per-user OMP runtime: spawn/respawn with `--resume`, per-user
/// command queueing with a cap, event dispatch (host tools, host URIs, streamed
/// text), idle-timeout teardown and shutdown. The `turnEnded` callback lets the
/// worker mark a command completed when a terminal `agent_end` arrives.
type SessionManager
    (
        options: SessionManagerOptions,
        profile: ProfileManager,
        workspaces: WorkspaceManager,
        omp: OmpProcessOptions,
        hostTools: HostToolExecutor,
        hostUris: HostUriResolver,
        enqueueOutbox: OutboxEnvelope -> Task<unit>,
        turnEnded: int64 -> Task<unit>,
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
                  StreamState = EventFormatter.initialState
                  EventLock = new SemaphoreSlim(1, 1) }

            runtimes[userId] <- rt
            rt

    /// Prompts the current command on the user's client, marking the runtime as
    /// busy. A fresh dequeued command is prompted while the session is idle, so
    /// no `streamingBehavior` is required.
    let promptCommand (rt: UserRuntime) (command: Command) : Task<unit> =
        task {
            match rt.Client with
            | Some client ->
                rt.CurrentCommand <- Some command
                rt.Busy <- true
                let message = command.Envelope.Payload
                let! result = client.PromptAsync(message, images = command.Envelope.Images)

                match result with
                | Ok _ -> ()
                | Error e -> logger.LogWarning("omp prompt failed: {Error}", e.Message)
            | None -> ()
        }

    /// Marks the runtime dead and clears in-memory state. The in-flight command
    /// is NOT lost: it remains `running` in the durable inbox with a lease, so
    /// the worker's `ExpireLeases` reclaims it (at-least-once). `SessionId` is
    /// preserved so the next spawn resumes the same OMP session.
    let onProcessExit (rt: UserRuntime) : unit =
        rt.Process <- None
        rt.Client <- None
        rt.Busy <- false
        rt.Queue.Clear()
        rt.CurrentCommand <- None
        rt.StreamState <- EventFormatter.initialState
        logger.LogWarning("omp process exited for user {UserId}", userInt rt.UserId)

    /// Dispatches one inbound frame for a user. Serialized per runtime so events
    /// are processed in arrival order. Host tool/URI requests are executed and
    /// answered; session events are formatted into outbox envelopes; a terminal
    /// `agent_end` triggers `turnEnded` and drains the next queued command.
    let handleEvent (rt: UserRuntime) (frame: JsonObject) : Task<unit> =
        task {
            rt.LastActivity <- now ()
            do! rt.EventLock.WaitAsync()

            try
                match RpcProtocol.classify frame with
                | FrameKind.HostToolCall ->
                    let! result = hostTools.TryExecute frame

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

                    let st, envelopes = EventFormatter.onEvent ctx rt.StreamState frame
                    rt.StreamState <- st

                    for e in envelopes do
                        do! enqueueOutbox e

                    match Json.getString "type" frame with
                    | Some "agent_start" -> rt.Busy <- true
                    | Some "agent_end" ->
                        let isTerminal =
                            match Json.getBool "isTerminal" frame with
                            | Some b -> b
                            | None -> true

                        // A non-terminal `agent_end` means the session will
                        // resume (maintenance/async delivery): the turn is NOT
                        // over, so the runtime stays busy — a prompt without
                        // `streamingBehavior` is rejected by OMP mid-stream.
                        if isTerminal then
                            rt.Busy <- false
                            let finished = rt.CurrentCommand
                            rt.CurrentCommand <- None
                            rt.StreamState <- EventFormatter.initialState

                            match finished with
                            | Some cmd -> do! turnEnded cmd.Id
                            | None -> ()

                            if rt.Queue.Count > 0 then
                                let next = rt.Queue.[0]
                                rt.Queue.RemoveAt(0)
                                do! promptCommand rt next
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

                client.EventReceived.Add(fun frame -> handleEvent rt frame |> ignore)

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
                    do! promptCommand rt command
                    rt.LastActivity <- now ()
                    return Ok()
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
    /// command respawns on demand with `--resume`.
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
                rt.Queue.Clear()
                rt.CurrentCommand <- None
                rt.StreamState <- EventFormatter.initialState

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
