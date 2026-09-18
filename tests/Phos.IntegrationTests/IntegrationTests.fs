module Phos.IntegrationTests.IntegrationTests

// FS3511 (state machine not statically compilable) is a performance-only
// warning emitted by the F# `task` builder when a nested `task {}` is invoked
// through a higher-order helper. It has no correctness impact and is expected
// for this test harness.
#nowarn "3511"

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open Microsoft.Extensions.Logging.Abstractions
open Phos.Core.DomainTypes
open Phos.Core.Chunker
open Phos.Core.ScheduleJobs
open Phos.Core.SchedulePolicy
open Phos.Storage
open Phos.Telegram
open Phos.Omp
open Phos.Scheduler

module Inbox = Phos.Core.InboxStateMachine

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

// fsharplint:disable ensureTailCallDiagnosticsInRecursiveFunctions

let private repoRoot () : string =
    let rec up (d: DirectoryInfo | null) =
        match d with
        | null -> failwith "Phos.sln not found"
        | d ->
            let sln = Path.Combine(d.FullName, "Phos.sln")

            if File.Exists sln then d.FullName else up d.Parent

    up (DirectoryInfo(AppContext.BaseDirectory))

// fsharplint:enable ensureTailCallDiagnosticsInRecursiveFunctions

let private tempDir () : string =
    let dir =
        Path.Combine(Path.GetTempPath(), "phos-it-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(dir) |> ignore
    dir

let private deleteDir (dir: string) =
    try
        if Directory.Exists dir then
            Directory.Delete(dir, true)
    with _ ->
        ()

/// Resolves the `omp` binary from `PHOS_OMP_PATH` or `PATH`. Fails (rather than
/// silently skipping) when omp is not installed, per the CI recipe.
let private findOmp () : string =
    let envPath = Environment.GetEnvironmentVariable "PHOS_OMP_PATH"

    match Option.ofObj envPath with
    | Some p when p <> "" -> p
    | _ ->
        let path = Environment.GetEnvironmentVariable "PATH"

        let dirs =
            match Option.ofObj path with
            | Some p -> p.Split(Path.PathSeparator)
            | None -> [||]

        match dirs |> Array.map (fun d -> Path.Combine(d, "omp")) |> Array.tryFind File.Exists with
        | Some p -> p
        | None -> failwith "omp not found — install @oh-my-pi/pi-coding-agent@18.1.19"

/// Polls `pred` until it returns true or `timeoutMs` elapses.
let private waitFor (timeoutMs: int) (pred: unit -> bool) : Task<bool> =
    task {
        let deadline = DateTimeOffset.UtcNow.AddMilliseconds(float timeoutMs)
        let mutable finished = false

        while not finished do
            if pred () then finished <- true
            elif DateTimeOffset.UtcNow >= deadline then finished <- true
            else do! Task.Delay 100

        return pred ()
    }

let private mkCommand (id: int64) (chatId: int64) (payload: string) : Command =
    { Id = id
      Envelope =
        { Origin = Telegram
          ExternalKey = None
          UserId = UserId 1L
          ChatId = ChatId chatId
          Payload = payload
          Priority = 0
          Images = [] }
      Status = Inbox.Status.Pending
      Attempts = 0
      MaxAttempts = 5
      LeaseUntil = None
      HeartbeatAt = None }

let private mkEnvelope (userId: int64) (chatId: int64) (payload: string) : CommandEnvelope =
    { Origin = Telegram
      ExternalKey = None
      UserId = UserId userId
      ChatId = ChatId chatId
      Payload = payload
      Priority = 0
      Images = [] }

// ---------------------------------------------------------------------------
// Fake transport / voice / client / outbox
// ---------------------------------------------------------------------------

type FakeTransport(?voiceBytes: byte[], ?photoBytes: byte[]) =
    let mutable sendCount = 0
    let mutable editCount = 0
    let mutable sentText = ""
    member _.SendCount = sendCount
    member _.EditCount = editCount
    member _.SentText = sentText

    interface ITelegramTransport with
        member _.Login() =
            task { return { BotId = 1L; Username = Some "bot" } }

        member _.SendMessage(t: SendTarget) =
            task {
                sendCount <- sendCount + 1
                sentText <- t.Text
                return Ok { RemoteMessageId = 1L }
            }

        member _.EditMessage (_: ChatId) (_: int64) (_: string) (_: TelegramEntity list) =
            task {
                editCount <- editCount + 1
                return ()
            }

        member _.DownloadVoice(_: VoiceRef) =
            task { return defaultArg voiceBytes [||] }

        member _.DownloadPhoto(_: PhotoRef) =
            task { return defaultArg photoBytes [||] }

        member _.SetReaction (_: ChatId) (_: int64) (_: string) = Task.FromResult(())

        member _.SetTyping(_: ChatId) = Task.FromResult(())

        member _.GetMessageSummary _ _ = task { return None }

        member _.DownloadMessagePhoto _ _ = task { return None }

        member _.GetHistory _ _ _ = task { return [] }

type FakeVoiceProcessor(result: Result<string, string>) =
    interface IVoiceProcessor with
        member _.ProcessAsync(_: VoiceRef) = task { return result }

type FakeOutbox() =
    let entries = ResizeArray<OutboxEntry>()
    member _.Entries = entries

    interface IMessageOutbox with
        member _.Insert
            (commandId: int64)
            (chunkIndex: int)
            (chatId: ChatId)
            (randomId: int64)
            (payload: string)
            (entities: Entity list)
            =
            task {
                let e =
                    { Id = int64 entries.Count
                      CommandId = commandId
                      ChunkIndex = chunkIndex
                      ChatId = chatId
                      RandomId = randomId
                      Payload = payload
                      Entities = entities
                      Status = Phos.Core.OutboxStateMachine.Status.Pending
                      Attempts = 0
                      MaxAttempts = 5
                      RemoteMessageId = None
                      CreatedAt = DateTimeOffset.UtcNow
                      UpdatedAt = DateTimeOffset.UtcNow }

                entries.Add e
                return e.Id
            }

        member _.NextPending() = task { return None }
        member _.BeginSend(_: int64) = Task.FromResult(())
        member _.MarkSent (_: int64) (_: int64) = Task.FromResult(())
        member _.MarkFailed(_: int64) = Task.FromResult(())
        member _.Retry(_: int64) = Task.FromResult(())
        member _.Defer (_: int64) (_: DateTimeOffset) = Task.FromResult(())
        member _.GetByRandomId(_: int64) = task { return None }
        member _.CountPending() = task { return 0 }

/// Minimal in-memory `IScheduleJobRepository` for integration tests. Schedule
/// host tools are not exercised by the integration scenarios, so the interface
/// is satisfied with sensible defaults.
type FakeScheduleRepo() =
    let mutable nextId = 1L

    interface IScheduleJobRepository with
        member _.Insert (draft: ScheduleJobDraft) (_: string option) =
            task {
                let id = nextId
                nextId <- nextId + 1L

                return
                    { Id = id
                      UserId = draft.UserId
                      ChatId = draft.ChatId
                      Prompt = draft.Prompt
                      CronExpr = draft.CronExpr
                      IntervalSeconds = draft.IntervalSeconds
                      AfterSeconds = draft.AfterSeconds
                      RunAt = draft.RunAt
                      Timezone = draft.Timezone
                      Catchup = draft.Catchup
                      Status = ScheduleStatus.Pending
                      NextRun = None
                      LastRunAt = None
                      LastError = None
                      CreatedAt = DateTimeOffset.UtcNow
                      UpdatedAt = DateTimeOffset.UtcNow }
            }

        member _.FindByToolCallId _ = task { return None }
        member _.GetById _ = task { return None }
        member _.ListForUser _ = task { return [] }
        member _.CountActiveForUser _ = task { return 0 }
        member _.Confirm _ = Task.FromResult(())
        member _.Cancel _ = Task.FromResult(())
        member _.Pause _ = Task.FromResult(())
        member _.Resume _ = Task.FromResult(())
        member _.Remove _ = Task.FromResult(())
        member _.RunNow _ = Task.FromResult(())
        member _.ExpirePending (_: DateTimeOffset) (_: DateTimeOffset) = task { return 0 }
        member _.PauseDueToErrors (_: int64) (_: string) = Task.FromResult(())
        member _.ClaimDueOccurrence _ = task { return None }

/// A controllable `IOmpRpcClient` used where the real omp's event emission is
/// not externally scriptable (e.g. scenario 2 terminal gating). Events are
/// raised by the test via `RaiseEvent`.
type FakeRpcClient(sessionId: string) =
    let eventReceived = Event<JsonObject>()
    let parseError = Event<string>()
    let lateFailure = Event<string * RpcError>()
    let mutable abortCount = 0
    let mutable promptCount = 0
    let mutable disposed = false
    let prompts = ResizeArray<string>()

    let stateObj () : JsonObject =
        let state = JsonObject()
        state["sessionId"] <- sessionId
        state["sessionFile"] <- "/tmp/" + sessionId + ".jsonl"
        state["isStreaming"] <- false
        state["queuedMessageCount"] <- 0
        state

    member _.AbortCount = abortCount
    member _.PromptCount = promptCount
    member _.Prompts = prompts
    member _.IsDisposed = disposed
    member _.RaiseEvent(frame: JsonObject) = eventReceived.Trigger frame

    interface IOmpRpcClient with
        member _.EventReceived = eventReceived.Publish
        member _.ParseError = parseError.Publish
        member _.LateFailure = lateFailure.Publish
        member _.IsDead = false

        member _.SendAsync(command: string, payload: JsonNode, ?id: string) : Task<Result<JsonNode, RpcError>> =
            task {
                match command with
                | "get_state" -> return Ok(stateObj () :> JsonNode)
                | _ -> return Ok(JsonObject() :> JsonNode)
            }

        member _.SendRawAsync(_: JsonObject) : Task<unit> = Task.FromResult(())

        member _.PromptAsync
            (message: string, ?streamingBehavior: string, ?images: string list, ?id: string)
            : Task<Result<JsonNode, RpcError>> =
            prompts.Add(message)
            promptCount <- promptCount + 1
            Task.FromResult(Ok(JsonObject() :> JsonNode))

        member _.AbortAsync() : Task<Result<JsonNode, RpcError>> =
            abortCount <- abortCount + 1
            Task.FromResult(Ok(JsonObject() :> JsonNode))

        member _.AbortAndPromptAsync(_: string) : Task<Result<JsonNode, RpcError>> =
            Task.FromResult(Ok(JsonObject() :> JsonNode))

        member _.FollowUpAsync(_: string) : Task<Result<JsonNode, RpcError>> =
            Task.FromResult(Ok(JsonObject() :> JsonNode))

        member _.GetStateAsync() : Task<Result<JsonNode, RpcError>> =
            Task.FromResult(Ok(stateObj () :> JsonNode))

        member _.GetLastAssistantTextAsync() : Task<Result<JsonNode, RpcError>> =
            Task.FromResult(Ok(JsonObject() :> JsonNode))

        member _.SetHostToolsAsync(_: RpcHostToolDefinition list) : Task<Result<JsonNode, RpcError>> =
            Task.FromResult(Ok(JsonObject() :> JsonNode))

        member _.SetHostUriSchemesAsync(_: RpcHostUriScheme list) : Task<Result<JsonNode, RpcError>> =
            Task.FromResult(Ok(JsonObject() :> JsonNode))

        member _.GetAvailableCommandsAsync() : Task<Result<JsonNode, RpcError>> =
            Task.FromResult(Ok(JsonObject() :> JsonNode))

        member _.Dispose() = disposed <- true

/// Records the `get_state` results for each spawned client (scenario 1) while
/// forwarding every call to a real `OmpRpcClient`.
type RecordingRpcClient(inner: IOmpRpcClient, states: ResizeArray<JsonObject>) =
    let eventReceived = Event<JsonObject>()
    let parseError = Event<string>()
    let lateFailure = Event<string * RpcError>()

    do
        inner.EventReceived.Add(eventReceived.Trigger)
        inner.ParseError.Add(parseError.Trigger)
        inner.LateFailure.Add(lateFailure.Trigger)

    interface IOmpRpcClient with
        member _.EventReceived = eventReceived.Publish
        member _.ParseError = parseError.Publish
        member _.LateFailure = lateFailure.Publish
        member _.IsDead = inner.IsDead

        member _.SendAsync(command: string, payload: JsonNode, ?id: string) : Task<Result<JsonNode, RpcError>> =
            inner.SendAsync(command, payload, ?id = id)

        member _.SendRawAsync(frame: JsonObject) : Task<unit> = inner.SendRawAsync frame

        member _.PromptAsync
            (message: string, ?streamingBehavior: string, ?images: string list, ?id: string)
            : Task<Result<JsonNode, RpcError>> =
            inner.PromptAsync(message, ?streamingBehavior = streamingBehavior, ?images = images, ?id = id)

        member _.AbortAsync() : Task<Result<JsonNode, RpcError>> = inner.AbortAsync()

        member _.AbortAndPromptAsync(message: string) : Task<Result<JsonNode, RpcError>> =
            inner.AbortAndPromptAsync message

        member _.FollowUpAsync(message: string) : Task<Result<JsonNode, RpcError>> = inner.FollowUpAsync message

        member _.GetStateAsync() : Task<Result<JsonNode, RpcError>> =
            task {
                let! r = inner.GetStateAsync()

                match r with
                | Ok s -> states.Add(s :?> JsonObject)
                | Error _ -> ()

                return r
            }

        member _.GetLastAssistantTextAsync() : Task<Result<JsonNode, RpcError>> = inner.GetLastAssistantTextAsync()

        member _.SetHostToolsAsync(tools: RpcHostToolDefinition list) : Task<Result<JsonNode, RpcError>> =
            inner.SetHostToolsAsync tools

        member _.SetHostUriSchemesAsync(schemes: RpcHostUriScheme list) : Task<Result<JsonNode, RpcError>> =
            inner.SetHostUriSchemesAsync schemes

        member _.GetAvailableCommandsAsync() : Task<Result<JsonNode, RpcError>> = inner.GetAvailableCommandsAsync()
        member _.Dispose() = inner.Dispose()

// ---------------------------------------------------------------------------
// Context + fixture
// ---------------------------------------------------------------------------

type TestHooks() =
    let envelopes = ResizeArray<OutboxEnvelope>()
    let turnEnded = ResizeArray<int64 * TurnOutcome>()
    member _.Envelopes = envelopes
    member _.TurnEnded = turnEnded

    member _.Enqueue(e: OutboxEnvelope) : Task<unit> = task { envelopes.Add e }

    member _.TurnEndedF (id: int64) (outcome: TurnOutcome) : Task<unit> = task { turnEnded.Add(id, outcome) }

type FakeOmpContext =
    { Server: FakeLlmServer
      ProfileName: string
      Profile: ProfileManager
      WorkspaceRoot: string
      Workspaces: WorkspaceManager
      OmpPath: string
      Transport: FakeTransport
      Voice: FakeVoiceProcessor
      Hooks: TestHooks }

let private configTemplate: string =
    """
symbolPreset: unicode
memory:
  backend: mnemopi
mnemopi:
  noEmbeddings: true
  llmMode: none
autolearn:
  enabled: false
astGrep:
  enabled: false
security:
  enabled: false
modelRoles:
  default: fake/fake-model
  smol: fake/fake-model
  slow: fake/fake-model
  tiny: fake/fake-model
"""

let private modelsTemplate (baseUrl: string) : string =
    sprintf
        """
providers:
  fake:
    baseUrl: "%s"
    apiKey: "test"
    api: openai-completions
    models:
      - id: fake-model
        name: Fake Model
        reasoning: false
        input: [text]
        contextWindow: 200000
        maxTokens: 16384
        compat:
          supportsStore: false
"""
        baseUrl

let private makeSessionManager
    (ctx: FakeOmpContext)
    (maxQueue: int)
    (spawnProcess: (OmpProcessOptions -> Result<IOmpProcess, string>) option)
    (createClient: (Process -> JsonObject -> IOmpRpcClient) option)
    (turnEnded: (int64 -> TurnOutcome -> Task<unit>) option)
    : SessionManager =
    let options =
        { Profile = ctx.ProfileName
          SourceProfile = "deepseek"
          IdleTimeout = TimeSpan.FromMinutes 30.0
          MaxQueuePerUser = maxQueue }

    let ompOptions =
        { OmpPath = ctx.OmpPath
          Profile = ctx.ProfileName
          WorkspaceDir = ""
          SessionResume = None
          Tools = "read"
          ApprovalMode = "yolo"
          MaxTime = "1h"
          ExtraFlags = [ "--no-skills"; "--no-extensions"; "--no-rules"; "--model"; "fake/fake-model" ]
          ReadyTimeoutSeconds = 30 }

    let hostTools =
        HostToolExecutor(
            ctx.Transport,
            ctx.Voice,
            FakeScheduleRepo(),
            { MaxJobsPerUser = 20
              MinIntervalSeconds = 60
              MaxPromptLength = 2000 },
            NullLogger<HostToolExecutor>.Instance
        )

    let hostUris = HostUriResolver(ctx.Transport, NullLogger<HostUriResolver>.Instance)

    let turnEndedFn = defaultArg turnEnded ctx.Hooks.TurnEndedF

    SessionManager(
        options,
        ctx.Profile,
        ctx.Workspaces,
        ompOptions,
        hostTools,
        hostUris,
        ctx.Hooks.Enqueue,
        turnEndedFn,
        NullLogger<SessionManager>.Instance,
        ?spawnProcess = spawnProcess,
        ?createClient = createClient
    )

let private withFakeOmp (behavior: FakeLlmBehavior) (f: FakeOmpContext -> Task<'T>) : Task<'T> =
    task {
        use server = new FakeLlmServer(behavior)
        server.Start()
        let profileName = "phos-it-" + Guid.NewGuid().ToString("N")

        let ompRoot =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".omp")

        let profile = ProfileManager ompRoot

        match profile.EnsureProfileWith(profileName, modelsTemplate server.BaseUrl, configTemplate) with
        | Ok() -> ()
        | Error e -> failwithf "failed to create temp profile %s: %s" profileName e

        let workspaceRoot = tempDir ()
        let workspaces = WorkspaceManager(workspaceRoot)
        let transport = FakeTransport()
        let voice = FakeVoiceProcessor(Ok "transcribed")

        let ctx =
            { Server = server
              ProfileName = profileName
              Profile = profile
              WorkspaceRoot = workspaceRoot
              Workspaces = workspaces
              OmpPath = findOmp ()
              Transport = transport
              Voice = voice
              Hooks = TestHooks() }

        try
            return! f ctx
        finally
            deleteDir workspaceRoot
            deleteDir (Path.Combine(ompRoot, "profiles", profileName))
    }

let private withSessionManager
    (ctx: FakeOmpContext)
    (maxQueue: int)
    (spawnProcess: (OmpProcessOptions -> Result<IOmpProcess, string>) option)
    (createClient: (Process -> JsonObject -> IOmpRpcClient) option)
    (turnEnded: (int64 -> TurnOutcome -> Task<unit>) option)
    (f: SessionManager -> Task<'T>)
    : Task<'T> =
    task {
        let sm = makeSessionManager ctx maxQueue spawnProcess createClient turnEnded

        try
            return! f sm
        finally
            sm.Shutdown()
    }

// ---------------------------------------------------------------------------
// Scenario 1 — two users share one profile with isolated workspaces
// ---------------------------------------------------------------------------

[<Fact>]
let ``two users share one profile with isolated workspaces`` () =
    task {
        do!
            withFakeOmp (TextOnly "hello") (fun ctx ->
                let states = ResizeArray<JsonObject>()

                let createClient (p: Process) (rf: JsonObject) : IOmpRpcClient =
                    let inner = OmpRpcClient(p, NullLogger<OmpRpcClient>.Instance, readyFrame = rf)
                    RecordingRpcClient(inner, states) :> IOmpRpcClient

                withSessionManager ctx 10 None (Some createClient) None (fun sm ->
                    task {
                        let user1 = UserId 1L
                        let user2 = UserId 2L

                        let! r1 = sm.EnsureRuntime user1

                        match r1 with
                        | Ok() -> ()
                        | Error e -> failwithf "EnsureRuntime user1 failed: %s" e

                        let! r2 = sm.EnsureRuntime user2

                        match r2 with
                        | Ok() -> ()
                        | Error e -> failwithf "EnsureRuntime user2 failed: %s" e

                        // Both processes alive.
                        sm.IsRuntimeAlive user1 |> should be True
                        sm.IsRuntimeAlive user2 |> should be True

                        // Workspace dirs differ.
                        let ws1 = ctx.Workspaces.PathFor user1
                        let ws2 = ctx.Workspaces.PathFor user2
                        ws1 <> ws2 |> should be True

                        // APPEND_SYSTEM.md exists per workspace.
                        File.Exists(Path.Combine(ws1, ".omp", "APPEND_SYSTEM.md")) |> should be True

                        File.Exists(Path.Combine(ws2, ".omp", "APPEND_SYSTEM.md")) |> should be True

                        // get_state sessionId/sessionFile differ across users.
                        let! stateReady = waitFor 5000 (fun () -> states.Count >= 2)
                        stateReady |> should be True

                        let s1 = states.[0]
                        let s2 = states.[1]
                        Json.getString "sessionId" s1 <> Json.getString "sessionId" s2 |> should be True

                        Json.getString "sessionFile" s1 <> Json.getString "sessionFile" s2
                        |> should be True

                        // Sessions dir exists under profile sessions/<cwd-escaped> per user.
                        let sessionsDir =
                            Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                ".omp",
                                "profiles",
                                ctx.ProfileName,
                                "agent",
                                "sessions"
                            )

                        let escaped1 = ws1.Replace("/", "-")
                        let escaped2 = ws2.Replace("/", "-")
                        Directory.Exists(Path.Combine(sessionsDir, escaped1)) |> should be True
                        Directory.Exists(Path.Combine(sessionsDir, escaped2)) |> should be True

                        // Memory banks (mnemopi) are per-workspace -> distinct.
                        let memoriesRoot =
                            Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                ".omp",
                                "profiles",
                                ctx.ProfileName,
                                "agent",
                                "memories",
                                "mnemopi",
                                "banks"
                            )

                        if Directory.Exists memoriesRoot then
                            let banks = Directory.GetDirectories memoriesRoot |> Array.map Path.GetFileName

                            banks.Length |> should be (greaterThanOrEqualTo 1)

                        ()
                    }))
    }

// ---------------------------------------------------------------------------
// Scenario 2 — agent_end terminal gating
// ---------------------------------------------------------------------------

[<Fact>]
let ``agent_end terminal gating completes exactly once`` () =
    task {
        do!
            withFakeOmp (TextOnly "hello") (fun ctx ->
                // omp's agent_end emission is not externally scriptable via the
                // LLM endpoint, so a controllable client drives the gating
                // through the full SessionManager pipeline.
                let client = FakeRpcClient("sess-1")

                let createClient (_: Process) (_: JsonObject) : IOmpRpcClient = client :> IOmpRpcClient

                withSessionManager ctx 10 None (Some createClient) None (fun sm ->
                    task {
                        let user = UserId 1L
                        let! _ = sm.EnsureRuntime user

                        let cmd = mkCommand 7L 1L "hi"
                        let! r = sm.Prompt(user, cmd)

                        match r with
                        | Ok() -> ()
                        | Error e -> failwithf "Prompt1 setup failed: %s" e

                        // Non-terminal agent_end: must NOT complete.
                        let nonTerminal = JsonObject()
                        nonTerminal["type"] <- "agent_end"
                        nonTerminal["isTerminal"] <- false
                        do! sm.HandleEvent(user, nonTerminal)
                        ctx.Hooks.TurnEnded.Count |> should equal 0

                        // The command must still be in flight (busy) so the turn
                        // is not considered finished.
                        let agentStart = JsonObject()
                        agentStart["type"] <- "agent_start"
                        do! sm.HandleEvent(user, agentStart)

                        // Terminal agent_end: completes exactly once.
                        let terminal = JsonObject()
                        terminal["type"] <- "agent_end"
                        terminal["isTerminal"] <- true
                        do! sm.HandleEvent(user, terminal)
                        ctx.Hooks.TurnEnded.Count |> should equal 1
                        fst ctx.Hooks.TurnEnded.[0] |> should equal 7L

                        // A second terminal agent_end (e.g. from the real
                        // process) must not complete again.
                        do! sm.HandleEvent(user, terminal)
                        ctx.Hooks.TurnEnded.Count |> should equal 1

                        // Late frames after finalization must not be routed to
                        // the synthetic command 0/chat 0 context.
                        let lateDelta = JsonObject()
                        lateDelta["type"] <- "message_update"
                        let lateEvent = JsonObject()
                        lateEvent["type"] <- "text_delta"
                        lateEvent["delta"] <- "orphaned"
                        lateDelta["assistantMessageEvent"] <- lateEvent
                        do! sm.HandleEvent(user, lateDelta)
                        do! sm.HandleEvent(user, terminal)
                        ctx.Hooks.Envelopes |> should be Empty
                    }))
    }

// ---------------------------------------------------------------------------
// Scenario 3 — abort a running turn
// ---------------------------------------------------------------------------

[<Fact>]
let ``abort stops a running turn and the next prompt works`` () =
    task {
        do!
            withFakeOmp (SlowStream("first second third fourth fifth", 400)) (fun ctx ->
                withSessionManager ctx 10 None None None (fun sm ->
                    task {
                        let user = UserId 1L
                        let! _ = sm.EnsureRuntime user

                        let cmd1 = mkCommand 1L 1L "run slow"
                        let! r = sm.Prompt(user, cmd1)

                        match r with
                        | Ok() -> ()
                        | Error e -> failwithf "Prompt1 submit failed: %s" e

                        // Wait until the first delta streams (the fake emits the
                        // role chunk immediately, then words every 400ms).
                        let! sawDelta =
                            waitFor 5000 (fun () -> ctx.Hooks.Envelopes.Count >= 1 || (not (sm.IsRuntimeAlive user)))

                        sawDelta |> should be True

                        // Abort the running turn.
                        do! sm.Abort user

                        // Process must survive an abort.
                        let! alive = waitFor 5000 (fun () -> sm.IsRuntimeAlive user)
                        alive |> should be True

                        // omp v18.1.19 emits a terminal `agent_end` on abort, so
                        // the aborted command (id 1) is finalized.
                        let! aborted =
                            waitFor 5000 (fun () -> ctx.Hooks.TurnEnded |> Seq.exists (fun (i, _) -> i = 1L))

                        aborted |> should be True

                        // Next prompt is accepted by the session (the fake endpoint
                        // serves it again).
                        let cmd2 = mkCommand 2L 1L "after abort"
                        let! r2 = sm.Prompt(user, cmd2)

                        match r2 with
                        | Ok() -> ()
                        | Error e -> failwithf "Prompt2 submit failed: %s" e

                        let! served = waitFor 15000 (fun () -> ctx.Server.RequestCount > 1)

                        served |> should be True
                        ctx.Hooks.TurnEnded |> List.ofSeq |> List.map fst |> should contain 1L
                    }))
    }

// ---------------------------------------------------------------------------
// Scenario 4 — process kill -> respawn --resume without loss/dup
// ---------------------------------------------------------------------------

[<Fact>]
let ``process kill respawns with resume without loss or duplicate`` () =
    task {
        do!
            withFakeOmp (TextOnly "the answer is 42") (fun ctx ->
                let recorded = ResizeArray<OmpProcessOptions>()
                let mutable procRef: OmpProcess option = None

                let spawnProcess (o: OmpProcessOptions) : Result<IOmpProcess, string> =
                    recorded.Add o

                    match OmpProcess.Start(o, NullLogger<OmpProcess>.Instance) with
                    | Ok p ->
                        procRef <- Some p
                        Ok(p :> IOmpProcess)
                    | Error e -> Error e

                withSessionManager ctx 10 (Some spawnProcess) None None (fun sm ->
                    task {
                        let user = UserId 1L
                        let! _ = sm.EnsureRuntime user
                        recorded.Count |> should equal 1
                        recorded.[0].SessionResume |> should equal None

                        let cmd1 = mkCommand 1L 1L "what is the answer"
                        let! r = sm.Prompt(user, cmd1)

                        match r with
                        | Ok() -> ()
                        | Error e -> failwithf "Prompt1 retry failed: %s" e

                        let! c1 = waitFor 10000 (fun () -> ctx.Hooks.TurnEnded.Count >= 1)
                        c1 |> should be True

                        // Exactly one LLM request for the first prompt.
                        ctx.Server.RequestCount |> should equal 1
                        ctx.Server.Requests.[0] |> should equal [ "system"; "user" ]

                        // Let omp flush the completed turn to the session file
                        // before the hard kill, so the resumed process restores a
                        // consistent session.
                        do! Task.Delay 2000

                        // SIGKILL the process.
                        let p = procRef |> Option.defaultWith (fun () -> failwith "no process")
                        p.Process.Kill(entireProcessTree = true)

                        // Wait for the process to die and the runtime to react.
                        let! died = waitFor 5000 (fun () -> not (sm.IsRuntimeAlive user))
                        died |> should be True

                        // Next prompt respawns with --resume.
                        let cmd2 = mkCommand 2L 1L "follow up"
                        let! r2 = sm.Prompt(user, cmd2)

                        match r2 with
                        | Ok() -> ()
                        | Error e -> failwithf "Prompt2 retry failed: %s" e

                        recorded.Count |> should equal 2
                        recorded.[1].SessionResume |> should not' (equal None)

                        // The resumed process serves a NEW turn (cmd2) with the
                        // prior history, not a re-request of cmd1's prompt.
                        let! served =
                            waitFor 15000 (fun () -> ctx.Server.Requests |> Seq.exists (fun r -> List.length r >= 4))

                        served |> should be True
                    }))
    }

// ---------------------------------------------------------------------------
// Scenario 5 — queue overflow does not lose commands
// ---------------------------------------------------------------------------

[<Fact>]
let ``queue overflow does not lose commands`` () =
    task {
        do!
            withFakeOmp (TextOnly "first") (fun ctx ->
                let dbPath = Path.Combine(ctx.WorkspaceRoot, "it.db")

                let storageOpts =
                    { DatabasePath = dbPath
                      BusyTimeout = TimeSpan.FromSeconds 2.0
                      ReadPoolSize = 4
                      CheckpointEvery = 10 }

                task {
                    use exec = StorageExecutor.Create storageOpts
                    Schema.run storageOpts
                    let inbox = Repositories.commandInbox exec

                    let turnEndedFn (cmdId: int64) (_: TurnOutcome) : Task<unit> =
                        task { do! inbox.MarkCompleted cmdId }

                    let runScenario (sm: SessionManager) : Task<unit> =
                        task {
                            let user = UserId 1L
                            let! _ = sm.EnsureRuntime user

                            // Insert 5 commands (durable, pending).
                            let ids =
                                [ 1L; 2L; 3L; 4L; 5L ]
                                |> List.map (fun i ->
                                    inbox.Insert(mkEnvelope 1L i (sprintf "cmd%d" i)).GetAwaiter().GetResult())

                            // Prompt all 5 via the SessionManager queue (cap 2).
                            let promptResults =
                                ids
                                |> List.map (fun id ->
                                    let c = inbox.GetById(id).GetAwaiter().GetResult().Value
                                    sm.Prompt(user, c).GetAwaiter().GetResult())

                            // The first prompt is busy; the next two are queued; the
                            // overflow two are rejected with "queue full" (not lost).
                            let overflow = promptResults |> List.filter (fun r -> r = Error "queue full")
                            overflow.Length |> should be (greaterThanOrEqualTo 2)

                            // No command is lost: all five remain durable in the inbox.
                            let! pending = inbox.CountPending()
                            pending |> should equal 5
                        }

                    do! withSessionManager ctx 2 None None (Some turnEndedFn) runScenario
                })
    }

// ---------------------------------------------------------------------------
// Host tool roundtrip
// ---------------------------------------------------------------------------

[<Fact>]
let ``host tool call roundtrips through the executor`` () =
    task {
        do!
            withFakeOmp
                (ToolCall("tg_send_message", "{\"chat_id\": 1, \"text\": \"hello\"}", "done after tool"))
                (fun ctx ->
                    withSessionManager ctx 10 None None None (fun sm ->
                        task {
                            let user = UserId 1L
                            let! _ = sm.EnsureRuntime user

                            let cmd = mkCommand 1L 1L "send hello"
                            let! r = sm.Prompt(user, cmd)

                            match r with
                            | Ok() -> ()
                            | Error e -> failwithf "Prompt2 setup failed: %s" e

                            let! completed = waitFor 15000 (fun () -> ctx.Hooks.TurnEnded.Count >= 1)
                            completed |> should be True

                            // The transport was invoked once with the tool's text.
                            ctx.Transport.SendCount |> should equal 1
                            ctx.Transport.SentText |> should equal "hello"

                            // The final assistant text was delivered as an outbox envelope.
                            let finalPayloads =
                                ctx.Hooks.Envelopes |> Seq.map (fun e -> e.Payload) |> Seq.toList

                            finalPayloads
                            |> List.exists (fun p -> p.Contains "done after tool")
                            |> should be True
                        }))
    }

// ---------------------------------------------------------------------------
// Scheduler E2E — prompt-driven loop (add → confirm → run_now → tick → inbox → restart → remove)
// ---------------------------------------------------------------------------

/// Extracts the concatenated text of a `host_tool_result` content array.
let private resultText (r: JsonObject) : string =
    let content =
        Json.getObject "result" r
        |> Option.bind (fun res -> Json.getArray "content" res)
        |> Option.defaultValue (JsonArray())

    let texts =
        [ for item in content do
              match item with
              | :? JsonObject as o ->
                  match Json.getString "text" o with
                  | Some t -> yield t
                  | None -> ()
              | _ -> () ]

    String.concat "\n" texts

/// Builds a `host_tool_call` frame.
let private mkToolCall (id: string) (toolCallId: string) (toolName: string) (args: JsonObject) : JsonObject =
    let frame = JsonObject()
    frame["type"] <- "host_tool_call"
    frame["id"] <- id
    frame["toolCallId"] <- toolCallId
    frame["toolName"] <- toolName
    frame["arguments"] <- (args :> JsonNode)
    frame

[<Fact>]
let ``scheduler prompt-driven loop end to end`` () =
    task {
        let dir = tempDir ()
        let mutable execRef: StorageExecutor option = None

        try
            let dbPath = Path.Combine(dir, "phos.db")

            let storageOpts =
                { DatabasePath = dbPath
                  BusyTimeout = TimeSpan.FromSeconds 2.0
                  ReadPoolSize = 4
                  CheckpointEvery = 10 }

            let exec = StorageExecutor.Create storageOpts
            execRef <- Some exec
            Schema.run storageOpts

            let jobs = Repositories.scheduleJobRepository exec
            let inbox = Repositories.commandInbox exec

            let quota =
                { MaxJobsPerUser = 20
                  MinIntervalSeconds = 60
                  MaxPromptLength = 2000 }

            let hostTools =
                HostToolExecutor(
                    FakeTransport(),
                    FakeVoiceProcessor(Ok "hi"),
                    jobs,
                    quota,
                    NullLogger<HostToolExecutor>.Instance
                )

            let mutable wakes = 0

            let scheduler =
                new SchedulerService(
                    jobs,
                    (fun () -> wakes <- wakes + 1),
                    { TickSeconds = 15
                      PendingTtlHours = 24
                      MaxFailedTicks = 3 },
                    NullLogger<SchedulerService>.Instance
                )

            // (a) schedule_add creates a PENDING job and asks for confirmation.
            let addArgs = JsonObject()
            addArgs["prompt"] <- "напоминай каждый час"
            addArgs["interval_seconds"] <- 3600
            addArgs["timezone"] <- "UTC"
            addArgs["catch_up"] <- "once"

            let! addResult =
                hostTools.TryExecute(
                    UserId 1L,
                    Some(ChatId 7L),
                    Some Telegram,
                    mkToolCall "r1" "t1" "schedule_add" addArgs
                )

            match addResult with
            | Some r ->
                Json.getBool "isError" r |> should equal None
                (resultText r).Contains "ожидает подтверждения" |> should be True
            | None -> failwith "schedule_add: expected a host_tool_result"

            let! job = jobs.FindByToolCallId "t1"
            job |> should not' (be None)
            let jobId = job.Value.Id

            // (b) schedule_confirm activates the job (only after explicit confirmation).
            let confirmArgs = JsonObject()
            confirmArgs["job_id"] <- jobId

            let! confirmResult =
                hostTools.TryExecute(
                    UserId 1L,
                    Some(ChatId 7L),
                    Some Telegram,
                    mkToolCall "r2" "t2" "schedule_confirm" confirmArgs
                )

            match confirmResult with
            | Some r ->
                Json.getBool "isError" r |> should equal None
                (resultText r).Contains "активно" |> should be True
            | None -> failwith "schedule_confirm: expected a host_tool_result"

            // (c) schedule_run_now makes the active job due on the next tick.
            let runNowArgs = JsonObject()
            runNowArgs["job_id"] <- jobId

            let! runNowResult =
                hostTools.TryExecute(
                    UserId 1L,
                    Some(ChatId 7L),
                    Some Telegram,
                    mkToolCall "r3" "t3" "schedule_run_now" runNowArgs
                )

            match runNowResult with
            | Some r -> Json.getBool "isError" r |> should equal None
            | None -> failwith "schedule_run_now: expected a host_tool_result"

            // (d) One tick claims the due occurrence and wakes the worker.
            let! claimed = scheduler.RunOnce(DateTimeOffset.UtcNow.AddSeconds 1.0)
            claimed |> should be (greaterThanOrEqualTo 1)
            wakes |> should be (greaterThanOrEqualTo 1)

            // (e) The scheduled prompt is now a durable inbox command for the chat.
            let lease =
                { Until = DateTimeOffset.UtcNow.AddMinutes 5.0
                  HeartbeatAt = DateTimeOffset.UtcNow }

            let! cmd = inbox.ClaimNextForChat (ChatId 7L) lease
            cmd |> should not' (be None)
            cmd.Value.Envelope.Origin |> should equal Schedule
            cmd.Value.Envelope.Priority |> should equal 10
            cmd.Value.Envelope.Payload |> should startWith "[по расписанию]"
            cmd.Value.Envelope.Payload.Contains "напоминай каждый час" |> should be True

            // (f) Restart survival: a fresh executor/repo on the same DB does not
            // re-claim the already-advanced occurrence, and no duplicate command
            // is enqueued.
            exec.Dispose()
            execRef <- None

            let exec2 = StorageExecutor.Create storageOpts
            execRef <- Some exec2

            let jobs2 = Repositories.scheduleJobRepository exec2
            let inbox2 = Repositories.commandInbox exec2

            let scheduler2 =
                new SchedulerService(
                    jobs2,
                    (fun () -> wakes <- wakes + 1),
                    { TickSeconds = 15
                      PendingTtlHours = 24
                      MaxFailedTicks = 3 },
                    NullLogger<SchedulerService>.Instance
                )

            let! again = scheduler2.RunOnce(DateTimeOffset.UtcNow.AddSeconds 1.0)
            again |> should equal 0

            let! cmd2 = inbox2.ClaimNextForChat (ChatId 7L) lease
            cmd2 |> should equal None

            // (g) schedule_remove deletes the job; no further claims.
            let hostTools2 =
                HostToolExecutor(
                    FakeTransport(),
                    FakeVoiceProcessor(Ok "hi"),
                    jobs2,
                    quota,
                    NullLogger<HostToolExecutor>.Instance
                )

            let removeArgs = JsonObject()
            removeArgs["job_id"] <- jobId

            let! removeResult =
                hostTools2.TryExecute(
                    UserId 1L,
                    Some(ChatId 7L),
                    Some Telegram,
                    mkToolCall "r4" "t4" "schedule_remove" removeArgs
                )

            match removeResult with
            | Some r -> Json.getBool "isError" r |> should equal None
            | None -> failwith "schedule_remove: expected a host_tool_result"

            let! afterRemove = scheduler2.RunOnce(DateTimeOffset.UtcNow.AddSeconds 1.0)
            afterRemove |> should equal 0
        finally
            match execRef with
            | Some e -> e.Dispose()
            | None -> ()

            deleteDir dir
    }

[<Fact>]
let ``scheduler one-shot fires once end to end`` () =
    task {
        let dir = tempDir ()
        let mutable execRef: StorageExecutor option = None

        try
            let dbPath = Path.Combine(dir, "phos.db")

            let storageOpts =
                { DatabasePath = dbPath
                  BusyTimeout = TimeSpan.FromSeconds 2.0
                  ReadPoolSize = 4
                  CheckpointEvery = 10 }

            let exec = StorageExecutor.Create storageOpts
            execRef <- Some exec
            Schema.run storageOpts

            let jobs = Repositories.scheduleJobRepository exec
            let inbox = Repositories.commandInbox exec

            let quota =
                { MaxJobsPerUser = 20
                  MinIntervalSeconds = 60
                  MaxPromptLength = 2000 }

            let hostTools =
                HostToolExecutor(
                    FakeTransport(),
                    FakeVoiceProcessor(Ok "hi"),
                    jobs,
                    quota,
                    NullLogger<HostToolExecutor>.Instance
                )

            let mutable wakes = 0

            let scheduler =
                new SchedulerService(
                    jobs,
                    (fun () -> wakes <- wakes + 1),
                    { TickSeconds = 15
                      PendingTtlHours = 24
                      MaxFailedTicks = 3 },
                    NullLogger<SchedulerService>.Instance
                )

            // (a) schedule_add with after_seconds creates a PENDING one-shot job.
            let addArgs = JsonObject()
            addArgs["prompt"] <- "напиши мне"
            addArgs["after_seconds"] <- 300
            addArgs["timezone"] <- "UTC"

            let! addResult =
                hostTools.TryExecute(
                    UserId 1L,
                    Some(ChatId 7L),
                    Some Telegram,
                    mkToolCall "r1" "t1" "schedule_add" addArgs
                )

            match addResult with
            | Some r ->
                Json.getBool "isError" r |> should equal None
                (resultText r).Contains "сработает один раз" |> should be True
                (resultText r).Contains "ожидает подтверждения" |> should be True
            | None -> failwith "schedule_add (one-shot): expected a host_tool_result"

            let! job = jobs.FindByToolCallId "t1"
            job |> should not' (be None)
            job.Value.AfterSeconds |> should equal (Some 300)
            let jobId = job.Value.Id

            // (b) schedule_confirm activates the job.
            let confirmArgs = JsonObject()
            confirmArgs["job_id"] <- jobId

            let! confirmResult =
                hostTools.TryExecute(
                    UserId 1L,
                    Some(ChatId 7L),
                    Some Telegram,
                    mkToolCall "r2" "t2" "schedule_confirm" confirmArgs
                )

            match confirmResult with
            | Some r -> Json.getBool "isError" r |> should equal None
            | None -> failwith "schedule_confirm (one-shot): expected a host_tool_result"

            // (c) schedule_run_now makes the active one-shot due on the next tick.
            let runNowArgs = JsonObject()
            runNowArgs["job_id"] <- jobId

            let! runNowResult =
                hostTools.TryExecute(
                    UserId 1L,
                    Some(ChatId 7L),
                    Some Telegram,
                    mkToolCall "r3" "t3" "schedule_run_now" runNowArgs
                )

            match runNowResult with
            | Some r -> Json.getBool "isError" r |> should equal None
            | None -> failwith "schedule_run_now (one-shot): expected a host_tool_result"

            // (d) One tick claims the due occurrence, wakes the worker and completes.
            let! claimed = scheduler.RunOnce(DateTimeOffset.UtcNow.AddSeconds 1.0)
            claimed |> should be (greaterThanOrEqualTo 1)
            wakes |> should be (greaterThanOrEqualTo 1)

            // (e) The one-shot prompt is now a durable inbox command for the chat.
            let lease =
                { Until = DateTimeOffset.UtcNow.AddMinutes 5.0
                  HeartbeatAt = DateTimeOffset.UtcNow }

            let! cmd = inbox.ClaimNextForChat (ChatId 7L) lease
            cmd |> should not' (be None)
            cmd.Value.Envelope.Origin |> should equal Schedule
            cmd.Value.Envelope.Priority |> should equal 10
            cmd.Value.Envelope.Payload |> should startWith "[по расписанию]"
            cmd.Value.Envelope.Payload.Contains "напиши мне" |> should be True

            // (f) The one-shot job is auto-completed and does not fire again.
            let! completed = jobs.GetById jobId
            completed |> should not' (be None)
            completed.Value.Status |> should equal ScheduleStatus.Completed

            let! again = scheduler.RunOnce(DateTimeOffset.UtcNow.AddSeconds 1.0)
            again |> should equal 0

            // (g) Restart survival: a fresh executor/repo on the same DB does not
            // re-fire the completed one-shot, and no second command is enqueued.
            exec.Dispose()
            execRef <- None

            let exec2 = StorageExecutor.Create storageOpts
            execRef <- Some exec2

            let jobs2 = Repositories.scheduleJobRepository exec2
            let inbox2 = Repositories.commandInbox exec2

            let scheduler2 =
                new SchedulerService(
                    jobs2,
                    (fun () -> wakes <- wakes + 1),
                    { TickSeconds = 15
                      PendingTtlHours = 24
                      MaxFailedTicks = 3 },
                    NullLogger<SchedulerService>.Instance
                )

            let! afterRestart = scheduler2.RunOnce(DateTimeOffset.UtcNow.AddSeconds 1.0)
            afterRestart |> should equal 0

            let! cmd2 = inbox2.ClaimNextForChat (ChatId 7L) lease
            cmd2 |> should equal None
        finally
            match execRef with
            | Some e -> e.Dispose()
            | None -> ()

            deleteDir dir
    }
