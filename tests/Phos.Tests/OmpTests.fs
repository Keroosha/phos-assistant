module Phos.Tests.OmpTests

// FS3511 (state machine not statically compilable) is a performance-only
// warning emitted by the F# `task` builder when a nested `task {}` is invoked
// through a higher-order helper (`withClient`). It has no correctness impact
// and is expected for this test harness.
#nowarn "3511"

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text
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

module Inbox = Phos.Core.InboxStateMachine
module Outbox = Phos.Core.OutboxStateMachine

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

let private bunExe () : string =
    match Environment.GetEnvironmentVariable "PHOS_BUN" with
    | null
    | "" -> "bun"
    | p -> p

let private fakeServerPath () : string =
    Path.Combine(repoRoot (), "tests", "fakes", "fake-rpc-server.ts")

let private startFakeServer (scenario: string) : Process =
    let psi = ProcessStartInfo()
    psi.FileName <- bunExe ()
    psi.ArgumentList.Add(fakeServerPath ())
    psi.RedirectStandardInput <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.Environment["PHOS_FAKE_SCENARIO"] <- scenario
    let p = new Process()
    p.StartInfo <- psi

    try
        if not (p.Start()) then
            failwith "failed to start fake rpc server"

        p
    with ex ->
        p.Dispose()
        failwith ("bun required for RPC wire tests (set PHOS_BUN): " + ex.Message)

let private withClient (p: Process) (f: IOmpRpcClient -> Task<'T>) : Task<'T> =
    task {
        let client = OmpRpcClient(p, NullLogger<OmpRpcClient>.Instance) :> IOmpRpcClient

        try
            return! f client
        finally
            client.Dispose()
    }

let private tempDir () : string =
    let dir =
        Path.Combine(Path.GetTempPath(), "phos-omp-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(dir) |> ignore
    dir

let private mkWorkspaces () : WorkspaceManager =
    WorkspaceManager(Path.Combine(Path.GetTempPath(), "phos-ws-test-" + Guid.NewGuid().ToString("N")))

let private deleteDir (dir: string) =
    try
        if Directory.Exists dir then
            Directory.Delete(dir, true)
    with _ ->
        ()

let private mkCommandWithImages (id: int64) (chatId: int64) (payload: string) (images: string list) : Command =
    { Id = id
      Envelope =
        { Origin = Telegram
          ExternalKey = None
          UserId = UserId 1L
          ChatId = ChatId chatId
          Payload = payload
          Priority = 0
          Images = images }
      Status = Inbox.Status.Pending
      Attempts = 0
      MaxAttempts = 5
      LeaseUntil = None
      HeartbeatAt = None }

let private mkCommand (id: int64) (chatId: int64) (payload: string) : Command =
    mkCommandWithImages id chatId payload []

// ---------------------------------------------------------------------------
// Fake transport / voice / client / process
// ---------------------------------------------------------------------------

type FakeTransport
    (?voiceBytes: byte[], ?photoBytes: byte[], ?summaryResult: MessageSummary option, ?historyResult: HistoryEntry list)
    =
    let mutable sendCount = 0
    let mutable editCount = 0
    let mutable typingCount = 0
    let mutable summary = defaultArg summaryResult None
    let mutable history = defaultArg historyResult []
    let reactions = ResizeArray<int64 * string>()
    let historyCalls = ResizeArray<ChatId * int64 * int>()
    let mediaCalls = ResizeArray<MediaTarget>()
    let mutable failMedia = false
    member _.SendCount = sendCount
    member _.EditCount = editCount
    member _.TypingCount = typingCount
    member _.Reactions = reactions
    member _.MediaCalls = List.ofSeq mediaCalls

    member _.FailMedia
        with set (v: bool) = failMedia <- v

    member _.Summary
        with set (v: MessageSummary option) = summary <- v

    member _.History
        with set (v: HistoryEntry list) = history <- v

    member _.HistoryCalls = List.ofSeq historyCalls

    interface ITelegramTransport with
        member _.Login() =
            task { return { BotId = 1L; Username = Some "bot" } }

        member _.SendMessage(_: SendTarget) =
            task {
                sendCount <- sendCount + 1
                return Ok { RemoteMessageId = 1L }
            }

        member _.SendMedia(target: MediaTarget) =
            task {
                mediaCalls.Add target

                if failMedia then
                    return Error(Other "media send failed")
                else
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

        member _.SetReaction (_: ChatId) (messageId: int64) (emoji: string) =
            reactions.Add(messageId, emoji)
            Task.FromResult(())

        member _.SetTyping(_: ChatId) =
            typingCount <- typingCount + 1
            Task.FromResult(())

        member _.GetMessageSummary _ _ = task { return summary }

        member _.DownloadMessagePhoto _ _ = task { return None }

        member _.GetHistory (chat: ChatId) (beforeId: int64) (limit: int) =
            task {
                historyCalls.Add(chat, beforeId, limit)
                return history
            }

type FakeVoiceProcessor(result: Result<string, string>) =
    interface IVoiceProcessor with
        member _.ProcessAsync(_: VoiceRef) = task { return result }

/// In-memory `IScheduleJobRepository` for host-tool tests. Tracks inserts,
/// supports status transitions and a configurable active count / throw flag.
type FakeScheduleRepo() =
    let jobs = System.Collections.Generic.Dictionary<int64, ScheduleJob>()
    let toolCallIds = System.Collections.Generic.Dictionary<string, int64>()
    let mutable nextId = 1L
    let mutable countActive = 0
    let mutable insertCount = 0
    let mutable throwOnInsert = false

    let updateJob (id: int64) (f: ScheduleJob -> ScheduleJob) =
        match jobs.TryGetValue id with
        | true, j -> jobs[id] <- f j
        | _ -> ()

    member _.Jobs = jobs.Values |> Seq.toList
    member _.InsertCount = insertCount
    member _.JobCount = jobs.Count

    member _.PendingCount =
        jobs.Values
        |> Seq.filter (fun j -> j.Status = ScheduleStatus.Pending)
        |> Seq.length

    member _.SetCountActive(n: int) = countActive <- n
    member _.ThrowOnInsert = throwOnInsert <- true
    member _.ClearThrowOnInsert = throwOnInsert <- false

    member _.GetJob(id: int64) =
        jobs.TryGetValue id
        |> function
            | true, j -> Some j
            | _ -> None

    interface IScheduleJobRepository with
        member _.Insert (draft: ScheduleJobDraft) (origin: string option) =
            if throwOnInsert then
                failwith "fake schedule insert boom"
            else
                insertCount <- insertCount + 1
                let id = nextId
                nextId <- nextId + 1L

                let job =
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

                jobs[id] <- job

                match origin with
                | Some t -> toolCallIds[t] <- id
                | None -> ()

                Task.FromResult job

        member _.FindByToolCallId(t) =
            match toolCallIds.TryGetValue t with
            | true, id ->
                Task.FromResult(
                    jobs.TryGetValue id
                    |> function
                        | true, j -> Some j
                        | _ -> None
                )
            | _ -> Task.FromResult None

        member _.GetById(id) =
            Task.FromResult(
                jobs.TryGetValue id
                |> function
                    | true, j -> Some j
                    | _ -> None
            )

        member _.ListForUser(uid) =
            jobs.Values
            |> Seq.filter (fun j -> j.UserId = uid)
            |> Seq.toList
            |> Task.FromResult

        member _.CountActiveForUser(_) = Task.FromResult countActive

        member _.Confirm(id) =
            updateJob id (fun j ->
                if j.Status = ScheduleStatus.Pending then
                    { j with
                        Status = ScheduleStatus.Active
                        NextRun = Some(DateTimeOffset.UtcNow.AddSeconds 3600.0) }
                else
                    j)

            Task.FromResult(())

        member _.Cancel(id) =
            updateJob id (fun j ->
                if j.Status = ScheduleStatus.Pending then
                    { j with
                        Status = ScheduleStatus.Cancelled }
                else
                    j)

            Task.FromResult(())

        member _.Pause(id) =
            updateJob id (fun j ->
                if j.Status = ScheduleStatus.Active then
                    { j with
                        Status = ScheduleStatus.Paused }
                else
                    j)

            Task.FromResult(())

        member _.Resume(id) =
            updateJob id (fun j ->
                if j.Status = ScheduleStatus.Paused then
                    { j with
                        Status = ScheduleStatus.Active
                        NextRun = Some(DateTimeOffset.UtcNow.AddSeconds 3600.0) }
                else
                    j)

            Task.FromResult(())

        member _.Remove(id) =
            jobs.Remove(id) |> ignore
            Task.FromResult(())

        member _.RunNow(id) =
            updateJob id (fun j ->
                if j.Status = ScheduleStatus.Active then
                    { j with
                        NextRun = Some DateTimeOffset.UtcNow }
                else
                    j)

            Task.FromResult(())

        member _.ExpirePending (_: DateTimeOffset) (_: DateTimeOffset) = Task.FromResult 0

        member _.PauseDueToErrors (id: int64) (err: string) =
            updateJob id (fun j ->
                if j.Status = ScheduleStatus.Active then
                    { j with
                        Status = ScheduleStatus.Paused
                        LastError = Some err }
                else
                    j)

            Task.FromResult(())

        member _.ClaimDueOccurrence(_) = Task.FromResult None

let private defaultQuota: ScheduleQuota =
    { MaxJobsPerUser = 20
      MinIntervalSeconds = 60
      MaxPromptLength = 2000 }

type FakeOmpProcess() =
    let exited = Event<unit>()
    let mutable killed = false
    member _.RaiseExit() = exited.Trigger()
    member _.Killed = killed

    interface IOmpProcess with
        member _.Process = new Process()
        member _.ReadyFrame = JsonObject()
        member _.Exited = exited.Publish
        member _.IsDead = false
        member _.Kill() = killed <- true

type FakeRpcClient(sessionId: string) =
    let eventReceived = Event<JsonObject>()
    let parseError = Event<string>()
    let lateFailure = Event<string * RpcError>()
    let mutable abortCount = 0
    let mutable promptCount = 0
    let mutable disposed = false
    let prompts = ResizeArray<string>()
    let promptIds = ResizeArray<string option>()
    let imagePrompts = ResizeArray<string list>()

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
    member _.PromptIds = List.ofSeq promptIds
    member _.ImagePrompts = List.ofSeq imagePrompts
    member _.IsDisposed = disposed
    member _.RaiseEvent(frame: JsonObject) = eventReceived.Trigger frame
    member _.RaiseLateFailure(id: string, err: RpcError) = lateFailure.Trigger(id, err)

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
            promptIds.Add id
            imagePrompts.Add(defaultArg images [])
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

/// RPC client whose per-command results can be scripted, so error paths in the
/// `SessionManager` (get_state / set_host_tools / set_host_uri_schemes / prompt
/// failures) can be exercised deterministically.
type ScriptedRpcClient() =
    let eventReceived = Event<JsonObject>()
    let parseError = Event<string>()
    let lateFailure = Event<string * RpcError>()
    let mutable getStateResult: Result<JsonNode, RpcError> = Ok(JsonObject())
    let mutable setToolsResult: Result<JsonNode, RpcError> = Ok(JsonObject())
    let mutable setUrisResult: Result<JsonNode, RpcError> = Ok(JsonObject())
    let mutable promptResult: Result<JsonNode, RpcError> = Ok(JsonObject())
    let mutable promptCount = 0
    let mutable promptIds: string option list = []
    let mutable abortCount = 0
    let mutable disposed = false

    let error (command: string) (message: string) : RpcError =
        { Command = command
          Code = None
          Message = message }

    member _.GetStateResult
        with get () = getStateResult
        and set v = getStateResult <- v

    member _.SetToolsResult
        with get () = setToolsResult
        and set v = setToolsResult <- v

    member _.SetUrisResult
        with get () = setUrisResult
        and set v = setUrisResult <- v

    member _.PromptResult
        with get () = promptResult
        and set v = promptResult <- v

    member _.PromptCount = promptCount
    member _.PromptIds = List.rev promptIds
    member _.AbortCount = abortCount
    member _.IsDisposed = disposed
    member _.RaiseEvent(frame: JsonObject) = eventReceived.Trigger frame
    member _.RaiseLateFailure(id: string, err: RpcError) = lateFailure.Trigger(id, err)

    interface IOmpRpcClient with
        member _.EventReceived = eventReceived.Publish
        member _.ParseError = parseError.Publish
        member _.LateFailure = lateFailure.Publish
        member _.IsDead = false

        member _.SendAsync(command: string, _: JsonNode, ?id: string) : Task<Result<JsonNode, RpcError>> =
            match command with
            | "get_state" -> Task.FromResult(getStateResult)
            | "set_host_tools" -> Task.FromResult(setToolsResult)
            | "set_host_uri_schemes" -> Task.FromResult(setUrisResult)
            | _ -> Task.FromResult(Ok(JsonObject() :> JsonNode))

        member _.SendRawAsync(_: JsonObject) : Task<unit> = Task.FromResult(())

        member _.PromptAsync
            (_: string, ?streamingBehavior: string, ?images: string list, ?id: string)
            : Task<Result<JsonNode, RpcError>> =
            promptCount <- promptCount + 1
            promptIds <- id :: promptIds
            Task.FromResult(promptResult)

        member _.AbortAsync() : Task<Result<JsonNode, RpcError>> =
            abortCount <- abortCount + 1
            Task.FromResult(Ok(JsonObject() :> JsonNode))

        member _.AbortAndPromptAsync(_: string) : Task<Result<JsonNode, RpcError>> =
            Task.FromResult(Ok(JsonObject() :> JsonNode))

        member _.FollowUpAsync(_: string) : Task<Result<JsonNode, RpcError>> =
            Task.FromResult(Ok(JsonObject() :> JsonNode))

        member _.GetStateAsync() : Task<Result<JsonNode, RpcError>> = Task.FromResult(getStateResult)

        member _.GetLastAssistantTextAsync() : Task<Result<JsonNode, RpcError>> =
            Task.FromResult(Ok(JsonObject() :> JsonNode))

        member _.SetHostToolsAsync(_: RpcHostToolDefinition list) : Task<Result<JsonNode, RpcError>> =
            Task.FromResult(setToolsResult)

        member _.SetHostUriSchemesAsync(_: RpcHostUriScheme list) : Task<Result<JsonNode, RpcError>> =
            Task.FromResult(setUrisResult)

        member _.GetAvailableCommandsAsync() : Task<Result<JsonNode, RpcError>> =
            Task.FromResult(Ok(JsonObject() :> JsonNode))

        member _.Dispose() = disposed <- true

// ---------------------------------------------------------------------------
// Wire-contract tests (scripted fake RPC server)
// ---------------------------------------------------------------------------

[<Fact>]
let ``client negotiates protocol v2 when advertised`` () =
    task {
        use proc = startFakeServer "basic"

        do!
            withClient proc (fun client ->
                task {
                    let negotiated = TaskCompletionSource<int>()

                    client.EventReceived.Add(fun frame ->
                        match Json.getString "type" frame with
                        | Some "protocol_negotiated" ->
                            negotiated.TrySetResult(Json.getInt "version" frame |> Option.defaultValue 0)
                            |> ignore
                        | _ -> ())

                    negotiated.Task.Wait(5000) |> should be True
                    negotiated.Task.Result |> should equal 2
                })
    }

[<Fact>]
let ``send correlates responses by id even out of order`` () =
    task {
        use proc = startFakeServer "basic"

        do!
            withClient proc (fun client ->
                task {
                    let slow = client.SendAsync("get_state", JsonObject(), id = "slow")
                    let fast = client.SendAsync("get_state", JsonObject(), id = "fast")
                    let! fastResult = fast
                    let! slowResult = slow

                    match fastResult, slowResult with
                    | Ok f, Ok s ->
                        Json.getString "sessionId" (f :?> JsonObject) |> should equal (Some "sess-123")
                        Json.getString "sessionId" (s :?> JsonObject) |> should equal (Some "sess-123")
                    | _ -> failwith "expected both responses to succeed"
                })
    }

[<Fact>]
let ``failure response becomes typed error`` () =
    task {
        use proc = startFakeServer "basic"

        do!
            withClient proc (fun client ->
                task {
                    let! result = client.SendAsync("bash", JsonObject())

                    match result with
                    | Error e ->
                        e.Code |> should equal (Some "bash_error")
                        e.Message |> should equal "bash command rejected"
                    | Ok _ -> failwith "expected an error response"
                })
    }

[<Fact>]
let ``reassembles rpc_chunk frames into the original object`` () =
    task {
        use proc = startFakeServer "chunks"

        do!
            withClient proc (fun client ->
                task {
                    let! result = client.SendAsync("get_state", JsonObject())

                    match result with
                    | Ok state ->
                        Json.getString "sessionId" (state :?> JsonObject)
                        |> should equal (Some "sess-123")

                        Json.getString "sessionFile" (state :?> JsonObject)
                        |> should equal (Some "/tmp/sess-123.jsonl")
                    | Error e -> failwithf "expected ok, got %A" e
                })
    }

[<Fact>]
let ``reassembler rejects out-of-order chunks`` () =
    let r = RpcChunkReassembler(67_108_864L)
    let chunk1 = JsonObject()
    chunk1["chunkId"] <- "rpc-1"
    chunk1["index"] <- 1
    chunk1["count"] <- 2
    chunk1["byteLength"] <- 5
    chunk1["data"] <- Convert.ToBase64String(Encoding.UTF8.GetBytes "world")

    match r.TryAdd chunk1 with
    | Error _ -> ()
    | Ok _ -> failwith "expected an out-of-order rejection"

[<Fact>]
let ``reassembler rejects corrupted byteLength`` () =
    let r = RpcChunkReassembler(67_108_864L)
    let chunk0 = JsonObject()
    chunk0["chunkId"] <- "rpc-1"
    chunk0["index"] <- 0
    chunk0["count"] <- 2
    // byteLength is the FULL frame size; the declared total (6) is smaller
    // than the actual payload sum (10), so the sequence must be rejected.
    chunk0["byteLength"] <- 6
    chunk0["data"] <- Convert.ToBase64String(Encoding.UTF8.GetBytes "hello")

    match r.TryAdd chunk0 with
    | Ok None -> ()
    | _ -> failwith "expected Ok None for first chunk"

    let chunk1 = JsonObject()
    chunk1["chunkId"] <- "rpc-1"
    chunk1["index"] <- 1
    chunk1["count"] <- 2
    chunk1["byteLength"] <- 6
    chunk1["data"] <- Convert.ToBase64String(Encoding.UTF8.GetBytes "world")

    match r.TryAdd chunk1 with
    | Error _ -> ()
    | Ok _ -> failwith "expected a byteLength rejection"

[<Fact>]
let ``reassembler rejects interleaved chunk sequences`` () =
    let r = RpcChunkReassembler(67_108_864L)
    let a0 = JsonObject()
    a0["chunkId"] <- "rpc-a"
    a0["index"] <- 0
    a0["count"] <- 2
    a0["byteLength"] <- 5
    a0["data"] <- Convert.ToBase64String(Encoding.UTF8.GetBytes "hello")

    match r.TryAdd a0 with
    | Ok None -> ()
    | _ -> failwith "expected Ok None"

    let b0 = JsonObject()
    b0["chunkId"] <- "rpc-b"
    b0["index"] <- 0
    b0["count"] <- 2
    b0["byteLength"] <- 5
    b0["data"] <- Convert.ToBase64String(Encoding.UTF8.GetBytes "world")

    match r.TryAdd b0 with
    | Error _ -> ()
    | Ok _ -> failwith "expected an interleave rejection"

[<Fact>]
let ``agent_end event is dispatched to EventReceived`` () =
    task {
        use proc = startFakeServer "events"

        do!
            withClient proc (fun client ->
                task {
                    let agentEnd = TaskCompletionSource<JsonObject>()

                    client.EventReceived.Add(fun frame ->
                        match Json.getString "type" frame with
                        | Some "agent_end" -> agentEnd.TrySetResult(frame) |> ignore
                        | _ -> ())

                    let! result = client.PromptAsync "hello"

                    (match result with
                     | Ok _ -> true
                     | _ -> false)
                    |> should be True

                    agentEnd.Task.Wait(5000) |> should be True
                    let endFrame = agentEnd.Task.Result
                    Json.getBool "isTerminal" endFrame |> should equal (Some true)
                })
    }

[<Fact>]
let ``prompt_result event is dispatched to EventReceived`` () =
    task {
        use proc = startFakeServer "prompt_result"

        do!
            withClient proc (fun client ->
                task {
                    let promptResult = TaskCompletionSource<JsonObject>()

                    client.EventReceived.Add(fun frame ->
                        match Json.getString "type" frame with
                        | Some "prompt_result" -> promptResult.TrySetResult(frame) |> ignore
                        | _ -> ())

                    let! _ = client.PromptAsync "hello"

                    promptResult.Task.Wait(5000) |> should be True

                    Json.getBool "agentInvoked" promptResult.Task.Result
                    |> should equal (Some false)
                })
    }

[<Fact>]
let ``host_tool_call event raised and host_tool_result echoed`` () =
    task {
        use proc = startFakeServer "host"

        do!
            withClient proc (fun client ->
                task {
                    let toolCall = TaskCompletionSource<JsonObject>()
                    let echo = TaskCompletionSource<JsonObject>()

                    client.EventReceived.Add(fun frame ->
                        match Json.getString "type" frame with
                        | Some "host_tool_call" -> toolCall.TrySetResult(frame) |> ignore
                        | Some "echo" ->
                            echo.TrySetResult(Json.getObject "frame" frame |> Option.defaultValue frame)
                            |> ignore
                        | _ -> ())

                    let! _ = client.PromptAsync "x"

                    toolCall.Task.Wait(5000) |> should be True
                    let call = toolCall.Task.Result
                    Json.getString "id" call |> should equal (Some "host_1")
                    Json.getString "toolCallId" call |> should equal (Some "toolu_1")
                    Json.getString "toolName" call |> should equal (Some "tg_send_message")

                    // Produce and send a host_tool_result, then observe the echo.
                    let res = JsonObject()
                    res["type"] <- "host_tool_result"
                    res["id"] <- "host_1"

                    let content = JsonArray()
                    let item = JsonObject()
                    item["type"] <- "text"
                    item["text"] <- "sent"
                    content.Add(item)
                    let result = JsonObject()
                    result["content"] <- content
                    res["result"] <- result

                    do! client.SendRawAsync res

                    echo.Task.Wait(5000) |> should be True
                    Json.getString "id" echo.Task.Result |> should equal (Some "host_1")
                })
    }

[<Fact>]
let ``host_uri_request event raised and host_uri_result echoed`` () =
    task {
        use proc = startFakeServer "uri"

        do!
            withClient proc (fun client ->
                task {
                    let uriReq = TaskCompletionSource<JsonObject>()
                    let echo = TaskCompletionSource<JsonObject>()

                    client.EventReceived.Add(fun frame ->
                        match Json.getString "type" frame with
                        | Some "host_uri_request" -> uriReq.TrySetResult(frame) |> ignore
                        | Some "echo" ->
                            echo.TrySetResult(Json.getObject "frame" frame |> Option.defaultValue frame)
                            |> ignore
                        | _ -> ())

                    let! _ = client.PromptAsync "x"

                    uriReq.Task.Wait(5000) |> should be True
                    Json.getString "operation" uriReq.Task.Result |> should equal (Some "read")

                    let res = JsonObject()
                    res["type"] <- "host_uri_result"
                    res["id"] <- "uri_1"
                    res["content"] <- "AQID"
                    res["contentType"] <- "application/octet-stream"

                    do! client.SendRawAsync res

                    echo.Task.Wait(5000) |> should be True
                    Json.getString "id" echo.Task.Result |> should equal (Some "uri_1")
                })
    }

// ---------------------------------------------------------------------------
// HostToolExecutor / HostUriResolver
// ---------------------------------------------------------------------------

[<Fact>]
let ``executor sends message once and caches idempotently`` () =
    task {
        let transport = FakeTransport()
        let voice = FakeVoiceProcessor(Ok "hi")

        let executor =
            HostToolExecutor(
                transport,
                voice,
                mkWorkspaces (),
                FakeScheduleRepo(),
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_1"
        frame["toolCallId"] <- "toolu_1"
        frame["toolName"] <- "tg_send_message"
        let args = JsonObject()
        args["chat_id"] <- 1L
        args["text"] <- "hi"
        frame["arguments"] <- (args :> JsonNode)

        let! result = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        match result with
        | Some r ->
            Json.getString "type" r |> should equal (Some "host_tool_result")
            Json.getString "id" r |> should equal (Some "host_1")
            transport.SendCount |> should equal 1
        | None -> failwith "expected a result frame #1"

        // Replaying the same toolCallId must not repeat the side effect.
        let! result2 = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        match result2 with
        | Some r2 ->
            Json.getString "type" r2 |> should equal (Some "host_tool_result")
            transport.SendCount |> should equal 1
        | None -> failwith "expected a result frame #2"
    }

[<Fact>]
let ``executor reports isError for unknown tool`` () =
    task {
        let executor =
            HostToolExecutor(
                FakeTransport(),
                FakeVoiceProcessor(Ok "hi"),
                mkWorkspaces (),
                FakeScheduleRepo(),
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_2"
        frame["toolCallId"] <- "toolu_2"
        frame["toolName"] <- "nope"
        frame["arguments"] <- (JsonObject() :> JsonNode)

        let! result = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        match result with
        | Some r -> Json.getBool "isError" r |> should equal (Some true)
        | None -> failwith "expected a result frame #3"
    }

[<Fact>]
let ``executor returns none for non tool frame`` () =
    task {
        let executor =
            HostToolExecutor(
                FakeTransport(),
                FakeVoiceProcessor(Ok "hi"),
                mkWorkspaces (),
                FakeScheduleRepo(),
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let frame = JsonObject()
        frame["type"] <- "agent_start"
        let! result = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)
        result |> should equal None
    }

let private hostToolResultText (frame: JsonObject) : string =
    match Json.getObject "result" frame with
    | Some resultObj ->
        match Json.getArray "content" resultObj with
        | Some arr ->
            match arr |> Seq.tryHead with
            | Some(:? JsonObject as item) -> Json.getString "text" item |> Option.defaultValue ""
            | _ -> ""
        | None -> ""
    | None -> ""

[<Fact>]
let ``tg_send_photo sends a real workspace image as photo`` () =
    task {
        let transport = FakeTransport()
        let ws = WorkspaceManager(tempDir ())
        let wsDir = ws.PathFor(UserId 1L)
        Directory.CreateDirectory wsDir |> ignore
        let path = Path.Combine(wsDir, "x.png")
        File.WriteAllBytes(path, [| 1uy; 2uy; 3uy |])

        let executor =
            HostToolExecutor(
                transport,
                FakeVoiceProcessor(Ok "hi"),
                ws,
                FakeScheduleRepo(),
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_photo"
        frame["toolCallId"] <- "toolu_photo"
        frame["toolName"] <- "tg_send_photo"
        let args = JsonObject()
        args["chat_id"] <- 1L
        args["path"] <- path
        frame["arguments"] <- (args :> JsonNode)

        let! result = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        match result with
        | Some r ->
            Json.getBool "isError" r |> should equal None
            transport.MediaCalls |> List.length |> should equal 1
            let call = transport.MediaCalls.Head
            call.ChatId |> should equal (ChatId 1L)
            call.Media.Kind |> should equal MediaKind.Photo
            call.Media.MimeType |> should equal "image/png"

            call.Media.DataBase64
            |> should equal (Convert.ToBase64String [| 1uy; 2uy; 3uy |])
        | None -> failwith "expected a result frame for tg_send_photo"
    }

[<Fact>]
let ``tg_send_photo missing file returns error`` () =
    task {
        let transport = FakeTransport()
        let ws = WorkspaceManager(tempDir ())

        let executor =
            HostToolExecutor(
                transport,
                FakeVoiceProcessor(Ok "hi"),
                ws,
                FakeScheduleRepo(),
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_missing"
        frame["toolCallId"] <- "toolu_missing"
        frame["toolName"] <- "tg_send_photo"
        let args = JsonObject()
        args["chat_id"] <- 1L
        args["path"] <- Path.Combine(ws.PathFor(UserId 1L), "nope.png")
        frame["arguments"] <- (args :> JsonNode)

        let! result = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        match result with
        | Some r ->
            Json.getBool "isError" r |> should equal (Some true)
            Assert.Contains("файл не найден", hostToolResultText r)
            transport.MediaCalls |> should be Empty
        | None -> failwith "expected a result frame for missing file"
    }

[<Fact>]
let ``tg_send_video and tg_send_sticker route kinds and mimes`` () =
    task {
        let transport = FakeTransport()
        let ws = WorkspaceManager(tempDir ())
        let wsDir = ws.PathFor(UserId 1L)
        Directory.CreateDirectory wsDir |> ignore
        File.WriteAllBytes(Path.Combine(wsDir, "v.mp4"), [| 1uy |])
        File.WriteAllBytes(Path.Combine(wsDir, "s.webp"), [| 2uy |])

        let executor =
            HostToolExecutor(
                transport,
                FakeVoiceProcessor(Ok "hi"),
                ws,
                FakeScheduleRepo(),
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let callTool (name: string) (path: string) (id: string) : Task<JsonObject option> =
            let frame = JsonObject()
            frame["type"] <- "host_tool_call"
            frame["id"] <- id
            frame["toolCallId"] <- id
            frame["toolName"] <- name
            let args = JsonObject()
            args["chat_id"] <- 1L
            args["path"] <- path
            frame["arguments"] <- (args :> JsonNode)
            executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        let! r1 = callTool "tg_send_video" (Path.Combine(wsDir, "v.mp4")) "toolu_video"

        match r1 with
        | Some r -> Json.getBool "isError" r |> should equal None
        | None -> failwith "expected a result frame for tg_send_video"

        let! r2 = callTool "tg_send_sticker" (Path.Combine(wsDir, "s.webp")) "toolu_sticker"

        match r2 with
        | Some r -> Json.getBool "isError" r |> should equal None
        | None -> failwith "expected a result frame for tg_send_sticker"

        transport.MediaCalls |> List.length |> should equal 2
        let videoCall = transport.MediaCalls.[0]
        videoCall.Media.Kind |> should equal MediaKind.Video
        videoCall.Media.MimeType |> should equal "video/mp4"
        let stickerCall = transport.MediaCalls.[1]
        stickerCall.Media.Kind |> should equal MediaKind.Sticker
        stickerCall.Media.MimeType |> should equal "image/webp"
    }

[<Fact>]
let ``tg_send_photo resolves relative path against the workspace`` () =
    task {
        let transport = FakeTransport()
        let ws = WorkspaceManager(tempDir ())
        let wsDir = ws.PathFor(UserId 1L)
        Directory.CreateDirectory(Path.Combine(wsDir, "out")) |> ignore
        File.WriteAllBytes(Path.Combine(wsDir, "out", "x.png"), [| 9uy |])

        let executor =
            HostToolExecutor(
                transport,
                FakeVoiceProcessor(Ok "hi"),
                ws,
                FakeScheduleRepo(),
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_rel"
        frame["toolCallId"] <- "toolu_rel"
        frame["toolName"] <- "tg_send_photo"
        let args = JsonObject()
        args["chat_id"] <- 1L
        args["path"] <- "out/x.png"
        frame["arguments"] <- (args :> JsonNode)

        let! result = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        match result with
        | Some r ->
            Json.getBool "isError" r |> should equal None
            transport.MediaCalls |> List.length |> should equal 1

            transport.MediaCalls.Head.Media.DataBase64
            |> should equal (Convert.ToBase64String [| 9uy |])
        | None -> failwith "expected a result frame for relative path"
    }

[<Fact>]
let ``tg_send_photo maps transport send error`` () =
    task {
        let transport = FakeTransport()
        transport.FailMedia <- true
        let ws = WorkspaceManager(tempDir ())
        let wsDir = ws.PathFor(UserId 1L)
        Directory.CreateDirectory wsDir |> ignore
        let path = Path.Combine(wsDir, "x.png")
        File.WriteAllBytes(path, [| 1uy |])

        let executor =
            HostToolExecutor(
                transport,
                FakeVoiceProcessor(Ok "hi"),
                ws,
                FakeScheduleRepo(),
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_sendfail"
        frame["toolCallId"] <- "toolu_sendfail"
        frame["toolName"] <- "tg_send_photo"
        let args = JsonObject()
        args["chat_id"] <- 1L
        args["path"] <- path
        frame["arguments"] <- (args :> JsonNode)

        let! result = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        match result with
        | Some r ->
            Json.getBool "isError" r |> should equal (Some true)
            Assert.Contains("media send failed", hostToolResultText r)
        | None -> failwith "expected a result frame for media send failure"
    }

[<Fact>]
let ``uri resolver reads voice as base64 and rejects write`` () =
    task {
        let transport = FakeTransport(voiceBytes = [| 1uy; 2uy; 3uy |])
        let resolver = HostUriResolver(transport, NullLogger<HostUriResolver>.Instance)

        let readFrame = JsonObject()
        readFrame["type"] <- "host_uri_request"
        readFrame["id"] <- "uri_1"
        readFrame["operation"] <- "read"
        readFrame["url"] <- "tg://message/1/2"

        let! result = resolver.TryResolve readFrame

        match result with
        | Some r ->
            Json.getString "type" r |> should equal (Some "host_uri_result")
            Json.getString "contentType" r |> should equal (Some "application/octet-stream")

            Json.getString "content" r
            |> should equal (Some(Convert.ToBase64String [| 1uy; 2uy; 3uy |]))
        | None -> failwith "expected a result frame #4"

        let writeFrame = JsonObject()
        writeFrame["type"] <- "host_uri_request"
        writeFrame["id"] <- "uri_2"
        writeFrame["operation"] <- "write"
        writeFrame["url"] <- "tg://message/1/2"

        let! result2 = resolver.TryResolve writeFrame

        match result2 with
        | Some r2 -> Json.getBool "isError" r2 |> should equal (Some true)
        | None -> failwith "expected a result frame #5"
    }

[<Fact>]
let ``host uri history read returns json`` () =
    task {
        let date = DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.Zero)

        let history =
            [ { Id = 123L
                FromBot = false
                Date = date
                Summary = MessageSummary.Text "привет" }
              { Id = 124L
                FromBot = true
                Date = date
                Summary = MessageSummary.Voice } ]

        let transport = FakeTransport(historyResult = history)
        let resolver = HostUriResolver(transport, NullLogger<HostUriResolver>.Instance)

        let frame = JsonObject()
        frame["type"] <- "host_uri_request"
        frame["id"] <- "uri_h"
        frame["operation"] <- "read"
        frame["url"] <- "tg://history/42?limit=5&before=0"

        let! result = resolver.TryResolve frame

        match result with
        | Some r ->
            Json.getString "type" r |> should equal (Some "host_uri_result")
            Json.getString "contentType" r |> should equal (Some "application/json")

            let content = Json.getString "content" r |> Option.defaultValue ""

            match JsonNode.Parse content with
            | :? JsonArray as arr ->
                arr.Count |> should equal 2

                match arr.[0] with
                | :? JsonObject as first ->
                    Json.getInt64 "id" first |> should equal (Some 123L)
                    Json.getBool "fromBot" first |> should equal (Some false)
                    Json.getString "text" first |> should equal (Some "привет")
                    Json.getString "date" first |> should equal (Some "2026-09-14T18:00:00Z")
                | _ -> failwithf "expected history entry 0 to be an object: %A" arr.[0]

                match arr.[1] with
                | :? JsonObject as second ->
                    Json.getInt64 "id" second |> should equal (Some 124L)
                    Json.getString "kind" second |> should equal (Some "voice")
                | _ -> failwithf "expected history entry 1 to be an object: %A" arr.[1]
            | _ -> failwith "expected a JSON array in history"
        | None -> failwithf "expected history result frame (read): %A" result
    }

[<Fact>]
let ``host uri history parses limit and before`` () =
    task {
        let transport = FakeTransport(historyResult = [])
        let resolver = HostUriResolver(transport, NullLogger<HostUriResolver>.Instance)

        let frame = JsonObject()
        frame["type"] <- "host_uri_request"
        frame["id"] <- "uri_h2"
        frame["operation"] <- "read"
        frame["url"] <- "tg://history/42?limit=7&before=99"

        let! result = resolver.TryResolve frame

        match result with
        | Some _ -> ()
        | None -> failwithf "expected history result frame (parse): %A" result

        transport.HistoryCalls |> should equal [ (ChatId 42L, 99L, 7) ]
    }

[<Fact>]
let ``host uri history defaults and clamps limit`` () =
    task {
        let transport = FakeTransport(historyResult = [])
        let resolver = HostUriResolver(transport, NullLogger<HostUriResolver>.Instance)

        // No query: limit defaults to 50, before to 0.
        let frame = JsonObject()
        frame["type"] <- "host_uri_request"
        frame["id"] <- "uri_h3"
        frame["operation"] <- "read"
        frame["url"] <- "tg://history/7"

        let! _ = resolver.TryResolve frame
        transport.HistoryCalls |> should equal [ (ChatId 7L, 0L, 50) ]

        // Limit above the Telegram maximum is clamped to 100.
        let frame2 = JsonObject()
        frame2["type"] <- "host_uri_request"
        frame2["id"] <- "uri_h4"
        frame2["operation"] <- "read"
        frame2["url"] <- "tg://history/7?limit=200"

        let! _ = resolver.TryResolve frame2

        transport.HistoryCalls
        |> should equal [ (ChatId 7L, 0L, 50); (ChatId 7L, 0L, 100) ]
    }

// ---------------------------------------------------------------------------
// EventFormatter
// ---------------------------------------------------------------------------

[<Fact>]
let ``text deltas accumulate silently without status messages`` () =
    let ctx = { CommandId = 1L; ChatId = ChatId 5L }
    let frame = JsonObject()
    frame["type"] <- "message_update"
    let ev = JsonObject()
    ev["type"] <- "text_delta"
    ev["delta"] <- "Hello"
    frame["assistantMessageEvent"] <- (ev :> JsonNode)
    let st, envelopes, _ = EventFormatter.onEvent ctx EventFormatter.initialState frame
    envelopes |> should be Empty
    st.Accumulated |> should equal "Hello"

    let frame2 = JsonObject()
    frame2["type"] <- "message_update"
    let ev2 = JsonObject()
    ev2["type"] <- "text_delta"
    ev2["delta"] <- " world"
    frame2["assistantMessageEvent"] <- (ev2 :> JsonNode)
    let st2, env2, _ = EventFormatter.onEvent ctx st frame2
    env2 |> should be Empty
    st2.Accumulated |> should equal "Hello world"

[<Fact>]
let ``terminal agent_end chunks accumulated text`` () =
    let ctx = { CommandId = 1L; ChatId = ChatId 5L }
    let text = String.replicate 5000 "a"

    let st = { Accumulated = text; MediaCount = 0 }

    let frame = JsonObject()
    frame["type"] <- "agent_end"
    frame["isTerminal"] <- true
    let st2, envelopes, outcome = EventFormatter.onEvent ctx st frame
    envelopes.Length |> should be (greaterThan 1)
    outcome |> should equal (Some TurnOutcome.Completed)
    envelopes |> List.forall (fun e -> e.Payload.Length <= 4096) |> should be True
    // Markdown-free text produces no entities on the chunks.
    envelopes |> List.forall (fun e -> e.Entities |> List.isEmpty) |> should be True
    let joined = envelopes |> List.map (fun e -> e.Payload) |> String.concat ""
    joined |> should equal text
    st2.Accumulated |> should equal ""

[<Fact>]
let ``non terminal agent_end emits nothing`` () =
    let ctx = { CommandId = 1L; ChatId = ChatId 5L }

    let st =
        { Accumulated = "partial"
          MediaCount = 0 }

    let frame = JsonObject()
    frame["type"] <- "agent_end"
    frame["isTerminal"] <- false
    let _, envelopes, _ = EventFormatter.onEvent ctx st frame
    envelopes |> should be Empty

[<Fact>]
let ``terminal agent_end with empty text emits nothing`` () =
    let ctx = { CommandId = 1L; ChatId = ChatId 5L }

    let st = { Accumulated = ""; MediaCount = 0 }

    let frame = JsonObject()
    frame["type"] <- "agent_end"
    frame["isTerminal"] <- true
    let _, envelopes, _ = EventFormatter.onEvent ctx st frame
    envelopes |> should be Empty

[<Fact>]
let ``formatter image_end produces one photo media envelope`` () =
    let ctx = { CommandId = 1L; ChatId = ChatId 5L }
    let frame = JsonObject()
    frame["type"] <- "message_update"
    let ev = JsonObject()
    ev["type"] <- "image_end"
    let content = JsonObject()
    content["type"] <- "image"
    content["data"] <- "AQID"
    content["mimeType"] <- "image/png"
    ev["content"] <- (content :> JsonNode)
    frame["assistantMessageEvent"] <- (ev :> JsonNode)

    let st, envelopes, _ = EventFormatter.onEvent ctx EventFormatter.initialState frame
    envelopes |> List.length |> should equal 1
    let env = envelopes.Head
    env.CommandId |> should equal 1L
    env.ChatId |> should equal (ChatId 5L)
    env.Payload |> should equal ""
    env.Entities |> should be Empty
    env.ChunkIndex |> should equal -1

    match env.Media with
    | Some media ->
        media.Kind |> should equal MediaKind.Photo
        media.MimeType |> should equal "image/png"
        media.DataBase64 |> should equal "AQID"
    | None -> failwith "expected a media envelope"

    st.MediaCount |> should equal 1

[<Fact>]
let ``formatter image_end frames get distinct negative chunk indices`` () =
    let ctx = { CommandId = 1L; ChatId = ChatId 5L }

    let mkImageEnd (data: string) =
        let frame = JsonObject()
        frame["type"] <- "message_update"
        let ev = JsonObject()
        ev["type"] <- "image_end"
        let content = JsonObject()
        content["type"] <- "image"
        content["data"] <- data
        content["mimeType"] <- "image/png"
        ev["content"] <- (content :> JsonNode)
        frame["assistantMessageEvent"] <- (ev :> JsonNode)
        frame

    let st1, envs1, _ =
        EventFormatter.onEvent ctx EventFormatter.initialState (mkImageEnd "AQID")

    let st2, envs2, _ = EventFormatter.onEvent ctx st1 (mkImageEnd "BAUG")
    let all = envs1 @ envs2
    all |> List.length |> should equal 2
    all |> List.map (fun e -> e.ChunkIndex) |> should equal [ -1; -2 ]
    st2.MediaCount |> should equal 2

[<Fact>]
let ``formatter text_delta does not produce media`` () =
    let ctx = { CommandId = 1L; ChatId = ChatId 5L }
    let frame = JsonObject()
    frame["type"] <- "message_update"
    let ev = JsonObject()
    ev["type"] <- "text_delta"
    ev["delta"] <- "hello"
    frame["assistantMessageEvent"] <- (ev :> JsonNode)

    let st, envelopes, _ = EventFormatter.onEvent ctx EventFormatter.initialState frame
    envelopes |> should be Empty
    st.MediaCount |> should equal 0

// ---------------------------------------------------------------------------
// WorkspaceManager / ProfileManager
// ---------------------------------------------------------------------------

[<Fact>]
let ``workspace ensure creates dir and persona file`` () =
    let dir = tempDir ()

    try
        let ws = WorkspaceManager(Path.Combine(dir, "ws"))
        let user = UserId 42L

        match ws.Ensure user with
        | Ok wsDir -> File.Exists(Path.Combine(wsDir, ".omp", "APPEND_SYSTEM.md")) |> should be True
        | Error e -> failwith e

        // Idempotent.
        match ws.Ensure user with
        | Ok _ -> ()
        | Error e -> failwith e
    finally
        deleteDir dir

[<Fact>]
let ``workspace path derives from user id`` () =
    let dir = tempDir ()

    try
        let ws = WorkspaceManager(Path.Combine(dir, "ws"))
        ws.PathFor(UserId 7L) |> should equal (Path.Combine(dir, "ws", "7"))
    finally
        deleteDir dir

[<Fact>]
let ``workspace copies persona file when configured`` () =
    let dir = tempDir ()

    try
        let personaPath = Path.Combine(dir, "persona.md")
        File.WriteAllText(personaPath, "CUSTOM PERSONA")
        let ws = WorkspaceManager(Path.Combine(dir, "ws"), personaFile = personaPath)
        let user = UserId 1L

        let wsDir =
            match ws.Ensure user with
            | Ok d -> d
            | Error e -> failwith e

        let content = File.ReadAllText(Path.Combine(wsDir, ".omp", "APPEND_SYSTEM.md"))
        content.StartsWith("CUSTOM PERSONA", StringComparison.Ordinal) |> should be True

        content.Contains("PHOS_HOST_SCHEDULING_START", StringComparison.Ordinal)
        |> should be True

        content.Contains("run_at", StringComparison.Ordinal) |> should be True
    finally
        deleteDir dir

[<Fact>]
let ``workspace ensure upserts scheduling block in existing persona`` () =
    let dir = tempDir ()

    try
        let wsRoot = Path.Combine(dir, "ws")
        let appendDir = Path.Combine(wsRoot, "9", ".omp")
        Directory.CreateDirectory(appendDir) |> ignore
        let appendPath = Path.Combine(appendDir, "APPEND_SYSTEM.md")
        File.WriteAllText(appendPath, "CUSTOM PERSONA\n\nOld scheduling rules: use cron for dates.")

        let ws = WorkspaceManager wsRoot

        match ws.Ensure(UserId 9L) with
        | Ok actual -> actual |> should equal (Path.Combine(wsRoot, "9"))
        | Error e -> failwith e

        let first = File.ReadAllText appendPath
        first.Contains("CUSTOM PERSONA", StringComparison.Ordinal) |> should be True

        first.Contains("Old scheduling rules", StringComparison.Ordinal)
        |> should be True

        first.Contains("PHOS_HOST_SCHEDULING_START", StringComparison.Ordinal)
        |> should be True

        first.Contains("supersede", StringComparison.OrdinalIgnoreCase)
        |> should be True

        first.Contains("date", StringComparison.OrdinalIgnoreCase) |> should be True
        first.Contains("run_at", StringComparison.Ordinal) |> should be True
        first.Contains("timezone", StringComparison.OrdinalIgnoreCase) |> should be True

        first.Contains("confirmation", StringComparison.OrdinalIgnoreCase)
        |> should be True

        let marker = "<!-- PHOS_HOST_SCHEDULING_START -->"

        first.IndexOf(marker, StringComparison.Ordinal)
        |> should equal (first.LastIndexOf(marker, StringComparison.Ordinal))

        match ws.Ensure(UserId 9L) with
        | Ok actual -> actual |> should equal (Path.Combine(wsRoot, "9"))
        | Error e -> failwith e

        let second = File.ReadAllText appendPath
        second |> should equal first
    finally
        deleteDir dir

[<Fact>]
let ``workspace remove deletes dir`` () =
    let dir = tempDir ()

    try
        let ws = WorkspaceManager(Path.Combine(dir, "ws"))
        let user = UserId 1L
        ws.Ensure user |> ignore
        Directory.Exists(ws.PathFor user) |> should be True
        ws.Remove user
        Directory.Exists(ws.PathFor user) |> should be False
    finally
        deleteDir dir

[<Fact>]
let ``profile ensure creates config and copies source files`` () =
    let dir = tempDir ()

    try
        let ompRoot = Path.Combine(dir, "omp")
        let srcAgent = Path.Combine(ompRoot, "profiles", "deepseek", "agent")
        Directory.CreateDirectory(srcAgent) |> ignore
        File.WriteAllText(Path.Combine(srcAgent, "models.yml"), "models: {}")
        File.WriteAllText(Path.Combine(srcAgent, ".env"), "KEY=value")
        let pm = ProfileManager ompRoot

        match pm.EnsureProfile("phos", "deepseek") with
        | Ok() ->
            let agent = Path.Combine(ompRoot, "profiles", "phos", "agent")
            File.Exists(Path.Combine(agent, "config.yml")) |> should be True
            File.Exists(Path.Combine(agent, "models.yml")) |> should be True
            File.Exists(Path.Combine(agent, ".env")) |> should be True
            File.ReadAllText(Path.Combine(agent, "models.yml")) |> should equal "models: {}"
        | Error e -> failwith e
    finally
        deleteDir dir

[<Fact>]
let ``profile ensure errors on missing source`` () =
    let dir = tempDir ()

    try
        let pm = ProfileManager(Path.Combine(dir, "omp"))

        match pm.EnsureProfile("phos", "nope") with
        | Ok() -> failwith "expected an error"
        | Error e -> e.Contains("nope") |> should be True
    finally
        deleteDir dir

[<Fact>]
let ``profile ensure is idempotent`` () =
    let dir = tempDir ()

    try
        let ompRoot = Path.Combine(dir, "omp")
        let srcAgent = Path.Combine(ompRoot, "profiles", "deepseek", "agent")
        Directory.CreateDirectory(srcAgent) |> ignore
        File.WriteAllText(Path.Combine(srcAgent, "models.yml"), "models: {}")
        File.WriteAllText(Path.Combine(srcAgent, ".env"), "KEY=value")
        let pm = ProfileManager ompRoot
        pm.EnsureProfile("phos", "deepseek") |> ignore

        match pm.EnsureProfile("phos", "deepseek") with
        | Ok() -> ()
        | Error e -> failwith e
    finally
        deleteDir dir

[<Fact>]
let ``profile provisioning adopts source config with modelRoles`` () =
    let dir = tempDir ()

    try
        let ompRoot = Path.Combine(dir, "omp")
        let srcAgent = Path.Combine(ompRoot, "profiles", "deepseek", "agent")
        Directory.CreateDirectory(srcAgent) |> ignore
        File.WriteAllText(Path.Combine(srcAgent, "models.yml"), "models: {}")
        File.WriteAllText(Path.Combine(srcAgent, ".env"), "KEY=value")

        File.WriteAllText(
            Path.Combine(srcAgent, "config.yml"),
            "modelRoles:\n  default: vanbukin/DeepSeek-V4-Flash-Vision-Exp\n"
        )

        let pm = ProfileManager ompRoot

        match pm.EnsureProfile("phos", "deepseek") with
        | Ok() ->
            let agent = Path.Combine(ompRoot, "profiles", "phos", "agent")
            let config = File.ReadAllText(Path.Combine(agent, "config.yml"))
            config.Contains("modelRoles:") |> should be True
            config.Contains("vanbukin") |> should be True
        | Error e -> failwith e
    finally
        deleteDir dir

[<Fact>]
let ``profile ensure repairs config without modelRoles`` () =
    let dir = tempDir ()

    try
        let ompRoot = Path.Combine(dir, "omp")
        let srcAgent = Path.Combine(ompRoot, "profiles", "deepseek", "agent")
        let phosAgent = Path.Combine(ompRoot, "profiles", "phos", "agent")
        Directory.CreateDirectory(srcAgent) |> ignore
        Directory.CreateDirectory(phosAgent) |> ignore
        File.WriteAllText(Path.Combine(srcAgent, "models.yml"), "models: {}")
        File.WriteAllText(Path.Combine(srcAgent, ".env"), "KEY=value")

        // Existing phos profile created by the old template: no modelRoles.
        File.WriteAllText(Path.Combine(phosAgent, "config.yml"), "symbolPreset: unicode\n")

        File.WriteAllText(
            Path.Combine(srcAgent, "config.yml"),
            "modelRoles:\n  default: vanbukin/DeepSeek-V4-Flash-Vision-Exp\n"
        )

        let pm = ProfileManager ompRoot

        match pm.EnsureProfile("phos", "deepseek") with
        | Ok() ->
            File.ReadAllText(Path.Combine(phosAgent, "config.yml"))
            |> fun c -> c.Contains("modelRoles:") |> should be True
        | Error e -> failwith e
    finally
        deleteDir dir

[<Fact>]
let ``profile ensure keeps config with modelRoles unchanged`` () =
    let dir = tempDir ()

    try
        let ompRoot = Path.Combine(dir, "omp")
        let srcAgent = Path.Combine(ompRoot, "profiles", "deepseek", "agent")
        let phosAgent = Path.Combine(ompRoot, "profiles", "phos", "agent")
        Directory.CreateDirectory(srcAgent) |> ignore
        Directory.CreateDirectory(phosAgent) |> ignore
        File.WriteAllText(Path.Combine(srcAgent, "models.yml"), "models: {}")
        File.WriteAllText(Path.Combine(srcAgent, ".env"), "KEY=value")
        File.WriteAllText(Path.Combine(phosAgent, "config.yml"), "modelRoles:\n  default: custom/model\n")
        let pm = ProfileManager ompRoot

        match pm.EnsureProfile("phos", "deepseek") with
        | Ok() ->
            File.ReadAllText(Path.Combine(phosAgent, "config.yml"))
            |> fun c -> c.Contains("custom/model") |> should be True
        | Error e -> failwith e
    finally
        deleteDir dir

// ---------------------------------------------------------------------------
// SessionManager
// ---------------------------------------------------------------------------

let private createSessionManager
    (spawnProcess: OmpProcessOptions -> Result<IOmpProcess, string>)
    (createClient: Process -> JsonObject -> IOmpRpcClient)
    (turnEnded: int64 -> TurnOutcome -> Task<unit>)
    (nowFn: unit -> DateTimeOffset)
    : SessionManager =
    let tmp = tempDir ()
    let profile = ProfileManager(Path.Combine(tmp, "omp"))
    profile.EnsureProfileWith("phos", "models: {}", "config: {}") |> ignore
    let workspaces = WorkspaceManager(Path.Combine(tmp, "ws"))
    let transport = FakeTransport()
    let voice = FakeVoiceProcessor(Ok "hi")

    let hostTools =
        HostToolExecutor(
            transport,
            voice,
            workspaces,
            FakeScheduleRepo(),
            defaultQuota,
            NullLogger<HostToolExecutor>.Instance
        )

    let hostUris = HostUriResolver(transport, NullLogger<HostUriResolver>.Instance)
    let enqueueOutbox (_: OutboxEnvelope) : Task<unit> = Task.FromResult(())

    let options =
        { Profile = "phos"
          SourceProfile = "deepseek"
          IdleTimeout = TimeSpan.FromMinutes 30.0
          MaxQueuePerUser = 3 }

    let ompOptions =
        { OmpPath = "omp"
          Profile = "phos"
          WorkspaceDir = ""
          SessionResume = None
          Tools = "read"
          ApprovalMode = "yolo"
          MaxTime = "1h"
          ExtraFlags = []
          ReadyTimeoutSeconds = 30 }

    SessionManager(
        options,
        profile,
        workspaces,
        ompOptions,
        hostTools,
        hostUris,
        enqueueOutbox,
        turnEnded,
        NullLogger<SessionManager>.Instance,
        spawnProcess = spawnProcess,
        createClient = createClient,
        now = nowFn
    )

[<Fact>]
let ``prompt enqueues when busy and rejects when queue full`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = FakeRpcClient("sess-1")
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let createClient (_: Process) (_: JsonObject) : IOmpRpcClient = client :> IOmpRpcClient
        let mutable currentTime = DateTimeOffset.UtcNow
        let nowFn () = currentTime
        let turnEnded (_: int64) (_: TurnOutcome) : Task<unit> = Task.FromResult(())
        let sm = createSessionManager spawn createClient turnEnded nowFn
        let user = UserId 1L

        let! r0 = sm.EnsureRuntime user

        match r0 with
        | Ok() -> ()
        | Error e -> failwithf "expected ok, got %s (prompt #1)" e

        let! r1 = sm.Prompt(user, mkCommand 1L 1L "hello")

        match r1 with
        | Ok() -> ()
        | Error e -> failwithf "expected ok, got %s (prompt #2)" e

        client.PromptCount |> should equal 1

        let! r2 = sm.Prompt(user, mkCommand 2L 1L "two")

        match r2 with
        | Ok() -> ()
        | Error e -> failwithf "expected ok, got %s (prompt #3)" e

        let! r3 = sm.Prompt(user, mkCommand 3L 1L "three")

        match r3 with
        | Ok() -> ()
        | Error e -> failwithf "expected ok, got %s (prompt #4)" e

        let! r4 = sm.Prompt(user, mkCommand 4L 1L "four")

        match r4 with
        | Ok() -> ()
        | Error e -> failwithf "expected ok, got %s (prompt #5)" e

        let! r5 = sm.Prompt(user, mkCommand 5L 1L "five")

        match r5 with
        | Error e when e = "queue full" -> ()
        | _ -> failwithf "expected queue full, got %A" r5

        client.PromptCount |> should equal 1
    }

[<Fact>]
let ``prompt passes command images to the rpc client`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = FakeRpcClient("sess-img")
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let createClient (_: Process) (_: JsonObject) : IOmpRpcClient = client :> IOmpRpcClient
        let nowFn () = DateTimeOffset.UtcNow
        let turnEnded (_: int64) (_: TurnOutcome) : Task<unit> = Task.FromResult(())
        let sm = createSessionManager spawn createClient turnEnded nowFn
        let user = UserId 1L

        let! _ = sm.EnsureRuntime user
        let cmd = mkCommandWithImages 1L 1L "что на фото?" [ "AQID" ]

        let! r = sm.Prompt(user, cmd)

        match r with
        | Ok() -> ()
        | Error e -> failwithf "expected ok, got %s" e

        client.PromptCount |> should equal 1
        client.ImagePrompts |> should equal [ [ "AQID" ] ]
    }

[<Fact>]
let ``respawns with resume after process exit`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = FakeRpcClient("sess-1")
        let recorded = ResizeArray<OmpProcessOptions>()

        let spawn (o: OmpProcessOptions) : Result<IOmpProcess, string> =
            recorded.Add o
            Ok(fakeProc :> IOmpProcess)

        let createClient (_: Process) (_: JsonObject) : IOmpRpcClient = client :> IOmpRpcClient
        let nowFn () = DateTimeOffset.UtcNow
        let turnEnded (_: int64) (_: TurnOutcome) : Task<unit> = Task.FromResult(())
        let sm = createSessionManager spawn createClient turnEnded nowFn
        let user = UserId 1L

        let! _ = sm.EnsureRuntime user
        recorded.Count |> should equal 1
        recorded.[0].SessionResume |> should equal None

        fakeProc.RaiseExit()

        let! _ = sm.Prompt(user, mkCommand 2L 1L "hi")
        recorded.Count |> should equal 2
        recorded.[1].SessionResume |> should equal (Some "sess-1")
    }

[<Fact>]
let ``idle timeout kills and clears runtime`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = FakeRpcClient("sess-1")
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let createClient (_: Process) (_: JsonObject) : IOmpRpcClient = client :> IOmpRpcClient
        let mutable currentTime = DateTimeOffset.UtcNow
        let nowFn () = currentTime
        let turnEnded (_: int64) (_: TurnOutcome) : Task<unit> = Task.FromResult(())
        let sm = createSessionManager spawn createClient turnEnded nowFn
        let user = UserId 1L

        let! _ = sm.EnsureRuntime user
        currentTime <- currentTime.AddMinutes 31.0
        sm.IdleTimeoutCheck currentTime
        fakeProc.Killed |> should be True
        client.IsDisposed |> should be True
        sm.IsRuntimeAlive user |> should be False
    }

[<Fact>]
let ``abort calls client abort`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = FakeRpcClient("sess-1")
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let createClient (_: Process) (_: JsonObject) : IOmpRpcClient = client :> IOmpRpcClient
        let nowFn () = DateTimeOffset.UtcNow
        let turnEnded (_: int64) (_: TurnOutcome) : Task<unit> = Task.FromResult(())
        let sm = createSessionManager spawn createClient turnEnded nowFn
        let user = UserId 1L

        let! _ = sm.EnsureRuntime user
        do! sm.Abort user
        client.AbortCount |> should equal 1
    }

[<Fact>]
let ``terminal agent_end triggers turn ended`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = FakeRpcClient("sess-1")
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let createClient (_: Process) (_: JsonObject) : IOmpRpcClient = client :> IOmpRpcClient
        let nowFn () = DateTimeOffset.UtcNow
        let turnEndedId = ref None

        let turnEnded (cmdId: int64) (_: TurnOutcome) : Task<unit> =
            task { turnEndedId.Value <- Some cmdId }

        let sm = createSessionManager spawn createClient turnEnded nowFn
        let user = UserId 1L

        let! _ = sm.Prompt(user, mkCommand 7L 1L "hi")
        let frame = JsonObject()
        frame["type"] <- "agent_end"
        frame["isTerminal"] <- true
        do! sm.HandleEvent(user, frame)
        turnEndedId.Value |> should equal (Some 7L)
    }

[<Fact>]
let ``non terminal agent_end does not trigger turn ended`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = FakeRpcClient("sess-1")
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let createClient (_: Process) (_: JsonObject) : IOmpRpcClient = client :> IOmpRpcClient
        let nowFn () = DateTimeOffset.UtcNow
        let turnEndedId = ref None

        let turnEnded (cmdId: int64) (_: TurnOutcome) : Task<unit> =
            task { turnEndedId.Value <- Some cmdId }

        let sm = createSessionManager spawn createClient turnEnded nowFn
        let user = UserId 1L

        let! _ = sm.Prompt(user, mkCommand 8L 1L "hi")
        let frame = JsonObject()
        frame["type"] <- "agent_end"
        frame["isTerminal"] <- false
        do! sm.HandleEvent(user, frame)
        turnEndedId.Value |> should equal None
    }

// ---------------------------------------------------------------------------
// SessionManager error paths (scripted RPC client)
// ---------------------------------------------------------------------------

let private scriptedSm
    (spawn: OmpProcessOptions -> Result<IOmpProcess, string>)
    (client: ScriptedRpcClient)
    (turnEnded: int64 -> TurnOutcome -> Task<unit>)
    : SessionManager =
    let createClient (_: Process) (_: JsonObject) : IOmpRpcClient = client :> IOmpRpcClient
    let nowFn () = DateTimeOffset.UtcNow
    createSessionManager spawn createClient turnEnded nowFn

[<Fact>]
let ``spawn failure makes ensure runtime report error`` () =
    task {
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Error "no omp binary"
        let sm = scriptedSm spawn client (fun _ _ -> Task.FromResult(()))
        let user = UserId 1L

        let! r = sm.EnsureRuntime user

        match r with
        | Error e -> e |> should equal "no omp binary"
        | Ok() -> failwith "expected spawn error"
    }

[<Fact>]
let ``get_state failure still spawns a live runtime`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()

        client.GetStateResult <-
            Error
                { Command = "get_state"
                  Code = None
                  Message = "state boom" }

        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ _ -> Task.FromResult(()))

        let! r = sm.EnsureRuntime(UserId 1L)

        match r with
        | Ok() -> ()
        | Error e -> failwith e

        sm.IsRuntimeAlive(UserId 1L) |> should be True
    }

[<Fact>]
let ``set_host_tools failure still spawns a live runtime`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()

        client.SetToolsResult <-
            Error
                { Command = "set_host_tools"
                  Code = None
                  Message = "tools boom" }

        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ _ -> Task.FromResult(()))

        let! r = sm.EnsureRuntime(UserId 1L)

        match r with
        | Ok() -> ()
        | Error e -> failwith e

        sm.IsRuntimeAlive(UserId 1L) |> should be True
    }

[<Fact>]
let ``set_host_uri_schemes failure still spawns a live runtime`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()

        client.SetUrisResult <-
            Error
                { Command = "set_host_uri_schemes"
                  Code = None
                  Message = "uris boom" }

        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ _ -> Task.FromResult(()))

        let! r = sm.EnsureRuntime(UserId 1L)

        match r with
        | Ok() -> ()
        | Error e -> failwith e

        sm.IsRuntimeAlive(UserId 1L) |> should be True
    }

[<Fact>]
let ``prompt rpc failure is surfaced and no duplicate prompt follows`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()

        client.PromptResult <-
            Error
                { Command = "prompt"
                  Code = None
                  Message = "prompt boom" }

        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ _ -> Task.FromResult(()))

        let! r = sm.Prompt(UserId 1L, mkCommand 1L 1L "hi")

        // The synchronous rejection must reach the caller (the worker's
        // prompt-error path), not be swallowed.
        match r with
        | Ok() -> failwith "prompt rejection must be surfaced"
        | Error e -> e |> should equal "prompt boom"

        // Exactly one prompt attempt; the runtime is idle again so a fresh
        // command can be prompted (no wedged busy state).
        client.PromptCount |> should equal 1

        let! r2 = sm.Prompt(UserId 1L, mkCommand 2L 1L "again")

        match r2 with
        | Ok() -> failwith "second rejection must be surfaced"
        | Error _ -> ()

        client.PromptCount |> should equal 2
    }

[<Fact>]
let ``agent_start marks the runtime busy`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ _ -> Task.FromResult(()))
        let user = UserId 1L

        let! _ = sm.EnsureRuntime user
        let frame = JsonObject()
        frame["type"] <- "agent_start"
        do! sm.HandleEvent(user, frame)

        // A second prompt while busy must be queued, not prompted.
        let! r = sm.Prompt(user, mkCommand 2L 1L "two")

        match r with
        | Ok() -> ()
        | Error e -> failwith e

        client.PromptCount |> should equal 0
    }

[<Fact>]
let ``terminal agent_end with no current command does not call turn ended`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let turnEndedId = ref None

        let turnEnded (cmdId: int64) (_: TurnOutcome) : Task<unit> =
            task { turnEndedId.Value <- Some cmdId }

        let sm = scriptedSm spawn client turnEnded
        let user = UserId 1L
        let! _ = sm.EnsureRuntime user

        let frame = JsonObject()
        frame["type"] <- "agent_end"
        frame["isTerminal"] <- true
        do! sm.HandleEvent(user, frame)

        turnEndedId.Value |> should equal None
    }

[<Fact>]
let ``terminal agent_end drains the next queued command`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let turnEndedId = ref None

        let turnEnded (cmdId: int64) (_: TurnOutcome) : Task<unit> =
            task { turnEndedId.Value <- Some cmdId }

        let sm = scriptedSm spawn client turnEnded
        let user = UserId 1L

        let! _ = sm.Prompt(user, mkCommand 1L 1L "one")
        let! _ = sm.Prompt(user, mkCommand 2L 1L "two")
        client.PromptCount |> should equal 1

        let frame = JsonObject()
        frame["type"] <- "agent_end"
        frame["isTerminal"] <- true
        do! sm.HandleEvent(user, frame)

        turnEndedId.Value |> should equal (Some 1L)
        client.PromptCount |> should equal 2
    }

[<Fact>]
let ``abort for unknown user is a no-op`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ _ -> Task.FromResult(()))
        do! sm.Abort(UserId 99L)
        client.AbortCount |> should equal 0
    }

[<Fact>]
let ``handle_event for unknown user is a no-op`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ _ -> Task.FromResult(()))
        let frame = JsonObject()
        frame["type"] <- "agent_end"
        do! sm.HandleEvent(UserId 99L, frame)
        client.PromptCount |> should equal 0
    }

[<Fact>]
let ``is_runtime_alive false for unknown user`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ _ -> Task.FromResult(()))
        sm.IsRuntimeAlive(UserId 99L) |> should be False
    }

[<Fact>]
let ``idle timeout on an exited runtime is a no-op`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ _ -> Task.FromResult(()))
        let user = UserId 1L

        let! _ = sm.EnsureRuntime user
        fakeProc.RaiseExit()
        sm.IdleTimeoutCheck(DateTimeOffset.UtcNow.AddMinutes 31.0)
        sm.IsRuntimeAlive user |> should be False
    }

[<Fact>]
let ``shutdown disposes client and kills process`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ _ -> Task.FromResult(()))

        let! _ = sm.EnsureRuntime(UserId 1L)
        sm.Shutdown()
        client.IsDisposed |> should be True
        fakeProc.Killed |> should be True
    }

// ---------------------------------------------------------------------------
// OmpWorker
// ---------------------------------------------------------------------------

type FakeInbox() =
    let commands: ResizeArray<Command> = ResizeArray()
    let mutable nextId = 1L
    let mutable listPendingCalls = 0
    let mutable listPendingThrow = false
    member _.Commands = commands
    member _.ListPendingCalls = listPendingCalls

    /// When true, the next `ListPendingChatIds` call throws (simulating a
    /// transient scan failure) and then resets itself.
    member _.ThrowOnListPending
        with get () = listPendingThrow
        and set v = listPendingThrow <- v

    member this.InsertCommand(env: CommandEnvelope) : Command =
        let cmd =
            { Id = nextId
              Envelope = env
              Status = Inbox.Status.Pending
              Attempts = 0
              MaxAttempts = 5
              LeaseUntil = None
              HeartbeatAt = None }

        nextId <- nextId + 1L
        commands.Add cmd
        cmd

    member this.GetById(id: int64) : Task<Command option> =
        task { return commands |> Seq.tryFind (fun c -> c.Id = id) }

    interface ICommandInbox with
        member _.Insert(env: CommandEnvelope) =
            task {
                let cmd =
                    { Id = nextId
                      Envelope = env
                      Status = Inbox.Status.Pending
                      Attempts = 0
                      MaxAttempts = 5
                      LeaseUntil = None
                      HeartbeatAt = None }

                nextId <- nextId + 1L
                commands.Add cmd
                return cmd.Id
            }

        member _.ClaimNextForChat (chatId: ChatId) (lease: Lease) =
            task {
                match
                    commands
                    |> Seq.tryFind (fun c -> c.Envelope.ChatId = chatId && c.Status = Inbox.Status.Pending)
                with
                | Some c ->
                    let i = commands.FindIndex(fun x -> x.Id = c.Id)

                    let updated =
                        { c with
                            Status = Inbox.Status.Claimed
                            LeaseUntil = Some lease.Until
                            HeartbeatAt = Some lease.HeartbeatAt }

                    commands.[i] <- updated
                    return Some updated
                | None -> return None
            }

        member _.ClaimById (id: int64) (lease: Lease) =
            task {
                match commands |> Seq.tryFind (fun c -> c.Id = id && c.Status = Inbox.Status.Pending) with
                | Some c ->
                    let i = commands.FindIndex(fun x -> x.Id = id)

                    let updated =
                        { c with
                            Status = Inbox.Status.Claimed
                            LeaseUntil = Some lease.Until
                            HeartbeatAt = Some lease.HeartbeatAt }

                    commands.[i] <- updated
                    return Some updated
                | None -> return None
            }

        member _.GetById(id: int64) =
            task { return commands |> Seq.tryFind (fun c -> c.Id = id) }

        member _.MarkStarted(id: int64) =
            task {
                let i = commands.FindIndex(fun c -> c.Id = id)

                if i >= 0 then
                    commands.[i] <-
                        { commands.[i] with
                            Status = Inbox.Status.Running }
            }

        member _.MarkCompleted(id: int64) =
            task {
                let i = commands.FindIndex(fun c -> c.Id = id)

                if i >= 0 then
                    commands.[i] <-
                        { commands.[i] with
                            Status = Inbox.Status.Completed }
            }

        member _.MarkFailed(id: int64) =
            task {
                let i = commands.FindIndex(fun c -> c.Id = id)

                if i >= 0 then
                    let c = commands.[i]
                    let attempts = c.Attempts + 1

                    let status =
                        if attempts >= c.MaxAttempts then
                            Inbox.Status.DeadLetter
                        else
                            Inbox.Status.Failed

                    commands.[i] <-
                        { c with
                            Attempts = attempts
                            Status = status }
            }

        member _.Retry(id: int64) =
            task {
                let i = commands.FindIndex(fun c -> c.Id = id)

                if i >= 0 && commands.[i].Status = Inbox.Status.Failed then
                    commands.[i] <-
                        { commands.[i] with
                            Status = Inbox.Status.Pending
                            LeaseUntil = None
                            HeartbeatAt = None }
            }

        member _.MarkNeedsReview(id: int64) =
            task {
                let i = commands.FindIndex(fun c -> c.Id = id)

                if i >= 0 then
                    commands.[i] <-
                        { commands.[i] with
                            Status = Inbox.Status.NeedsReview }
            }

        member _.ReviewRetry(id: int64) =
            task {
                let i = commands.FindIndex(fun c -> c.Id = id)

                if i >= 0 && commands.[i].Status = Inbox.Status.NeedsReview then
                    commands.[i] <-
                        { commands.[i] with
                            Status = Inbox.Status.Pending
                            LeaseUntil = None
                            HeartbeatAt = None }
            }

        member _.Heartbeat (id: int64) (at: DateTimeOffset) (_leaseUntil: DateTimeOffset) =
            task {
                let i = commands.FindIndex(fun c -> c.Id = id)

                if i >= 0 then
                    commands.[i] <-
                        { commands.[i] with
                            HeartbeatAt = Some at }
            }

        member _.ExpireLeases(now: DateTimeOffset) =
            task {
                let mutable n = 0

                for i in 0 .. commands.Count - 1 do
                    let c = commands.[i]

                    match c.LeaseUntil with
                    | Some until when
                        (c.Status = Inbox.Status.Claimed || c.Status = Inbox.Status.Running)
                        && now >= until
                        ->
                        commands.[i] <-
                            { c with
                                Status = Inbox.Status.Pending
                                LeaseUntil = None
                                HeartbeatAt = None }

                        n <- n + 1
                    | _ -> ()

                return n
            }

        member _.CountPending() =
            task { return commands |> Seq.filter (fun c -> c.Status = Inbox.Status.Pending) |> Seq.length }

        member _.CountDeadLetter() =
            task {
                return
                    commands
                    |> Seq.filter (fun c -> c.Status = Inbox.Status.DeadLetter)
                    |> Seq.length
            }

        member _.ListPendingChatIds() =
            task {
                listPendingCalls <- listPendingCalls + 1

                if listPendingThrow then
                    listPendingThrow <- false
                    failwith "list exploded"

                let ids =
                    commands
                    |> Seq.filter (fun c ->
                        c.Status = Inbox.Status.Pending
                        || c.Status = Inbox.Status.Failed
                        || c.Status = Inbox.Status.NeedsReview)
                    |> Seq.map (fun c -> c.Envelope.ChatId)
                    |> Seq.distinct
                    |> Seq.toList

                return ids
            }

type FakeOutbox() =
    let entries = ResizeArray<OutboxEntry>()
    member _.Entries = entries

    interface IMessageOutbox with
        member _.Insert
            (
                commandId: int64,
                chunkIndex: int,
                chatId: ChatId,
                randomId: int64,
                payload: string,
                entities: Entity list,
                ?media: MediaPayload
            ) =
            task {
                match
                    entries
                    |> Seq.tryFind (fun e -> e.CommandId = commandId && e.ChunkIndex = chunkIndex)
                with
                | Some existing -> return existing.Id
                | None ->
                    let e =
                        { Id = int64 entries.Count
                          CommandId = commandId
                          ChunkIndex = chunkIndex
                          ChatId = chatId
                          RandomId = randomId
                          Payload = payload
                          Entities = entities
                          Media = media
                          Status = Outbox.Status.Pending
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

type FakeSessions() =
    let mutable promptResults: Result<unit, string> list = []
    let mutable abortCount = 0
    let mutable promptCalls = 0
    let mutable alive = true
    member _.AbortCount = abortCount
    member _.PromptCalls = promptCalls
    member _.SetPromptResults(r: Result<unit, string> list) = promptResults <- r
    member _.SetAlive(v: bool) = alive <- v

    interface IOmpSessionManager with
        member _.EnsureRuntime(_: UserId) = Task.FromResult(Ok())

        member _.Prompt(_: UserId, _: Command) =
            promptCalls <- promptCalls + 1

            match promptResults with
            | [] -> Task.FromResult(Ok())
            | r :: rest ->
                promptResults <- rest
                Task.FromResult r

        member _.Abort(_: UserId) =
            abortCount <- abortCount + 1
            Task.FromResult(())

        member _.HandleEvent(_: UserId, _: JsonObject) = Task.FromResult(())
        member _.IsRuntimeAlive(_: UserId) = alive
        member _.IdleTimeoutCheck(_: DateTimeOffset) = ()
        member _.Shutdown() = ()

let private mkWorkerWithTransport
    (inbox: ICommandInbox)
    (sessions: FakeSessions)
    (transport: FakeTransport)
    : OmpWorker =
    let outbox = FakeOutbox()
    let wake = WakeChannel()
    let heartbeats = ConcurrentDictionary<int64, CancellationTokenSource>()
    // FS0760 requires `new` for IDisposable; fsharplint's redundantNewKeyword
    // rule would flag it, so suppress that rule for this line.
    // fsharplint:disable redundantNewKeyword

    new OmpWorker(inbox, outbox, sessions, wake, heartbeats, transport, NullLogger<OmpWorker>.Instance)

// fsharplint:enable redundantNewKeyword

let private mkWorker (inbox: ICommandInbox) (sessions: FakeSessions) : OmpWorker =
    mkWorkerWithTransport inbox sessions (FakeTransport())

[<Fact>]
let ``worker acknowledges accepted command with eyes reaction`` () =
    task {
        let inbox = FakeInbox()
        let sessions = FakeSessions()
        let transport = FakeTransport()
        let worker = mkWorkerWithTransport inbox sessions transport
        let chatId = 42L
        let messageId = 1234567L

        // updateId = messageId XOR (chatId <<< 32) — mirrors UpdateModel.mkUpdateId
        let updateId = messageId ^^^ (chatId <<< 32)

        let cmd =
            inbox.InsertCommand
                { Origin = Telegram
                  ExternalKey = Some(sprintf "tg:%d" updateId)
                  UserId = UserId 1L
                  ChatId = ChatId chatId
                  Payload = "привет"
                  Priority = 0
                  Images = [] }

        do! worker.ProcessCommandForTest cmd
        transport.Reactions |> should haveCount 1
        let mid, emoji = transport.Reactions.[0]
        mid |> should equal messageId
        emoji |> should equal "👀"
        // The typing bubble is sent immediately on accept (and re-sent on the
        // 4s loop while the command is Running).
        transport.TypingCount |> should be (greaterThanOrEqualTo 1)
    }

[<Fact>]
let ``worker stop aborts and completes command`` () =
    task {
        let inbox = FakeInbox()
        let sessions = FakeSessions()
        let worker = mkWorker inbox sessions

        let cmd =
            inbox.InsertCommand
                { Origin = Telegram
                  ExternalKey = None
                  UserId = UserId 1L
                  ChatId = ChatId 1L
                  Payload = "/stop"
                  Priority = 0
                  Images = [] }

        do! worker.ProcessCommandForTest cmd
        sessions.AbortCount |> should equal 1
        let c = (inbox.GetById(cmd.Id).GetAwaiter().GetResult()) |> Option.get
        c.Status |> should equal Inbox.Status.Completed
    }

[<Fact>]
let ``worker prompt marks command started`` () =
    task {
        let inbox = FakeInbox()
        let sessions = FakeSessions()
        sessions.SetPromptResults [ Ok() ]
        let worker = mkWorker inbox sessions

        let cmd =
            inbox.InsertCommand
                { Origin = Telegram
                  ExternalKey = None
                  UserId = UserId 1L
                  ChatId = ChatId 1L
                  Payload = "hello"
                  Priority = 0
                  Images = [] }

        do! worker.ProcessCommandForTest cmd
        sessions.PromptCalls |> should equal 1
        let c = (inbox.GetById(cmd.Id).GetAwaiter().GetResult()) |> Option.get
        c.Status |> should equal Inbox.Status.Running
    }

[<Fact>]
let ``worker prompt error fails and retries`` () =
    task {
        let inbox = FakeInbox()
        let sessions = FakeSessions()
        sessions.SetPromptResults [ Error "boom" ]
        let worker = mkWorker inbox sessions

        let cmd =
            inbox.InsertCommand
                { Origin = Telegram
                  ExternalKey = None
                  UserId = UserId 1L
                  ChatId = ChatId 1L
                  Payload = "hello"
                  Priority = 0
                  Images = [] }

        do! worker.ProcessCommandForTest cmd
        let c = (inbox.GetById(cmd.Id).GetAwaiter().GetResult()) |> Option.get
        c.Status |> should equal Inbox.Status.Pending
        c.Attempts |> should equal 1
    }

[<Fact>]
let ``worker queue full leaves command durable`` () =
    task {
        let inbox = FakeInbox()
        let sessions = FakeSessions()
        sessions.SetPromptResults [ Error "queue full" ]
        let worker = mkWorker inbox sessions

        let cmd =
            inbox.InsertCommand
                { Origin = Telegram
                  ExternalKey = None
                  UserId = UserId 1L
                  ChatId = ChatId 1L
                  Payload = "hello"
                  Priority = 0
                  Images = [] }

        do! worker.ProcessCommandForTest cmd
        let c = (inbox.GetById(cmd.Id).GetAwaiter().GetResult()) |> Option.get
        c.Status |> should equal Inbox.Status.Pending
    }

// ---------------------------------------------------------------------------
// OmpProcess
// ---------------------------------------------------------------------------

let private ompOpts (ompPath: string) (readySecs: int) : OmpProcessOptions =
    { OmpPath = ompPath
      Profile = "test"
      WorkspaceDir = Path.GetTempPath()
      SessionResume = None
      Tools = "read,grep"
      ApprovalMode = "yolo"
      MaxTime = "1h"
      ExtraFlags = []
      ReadyTimeoutSeconds = readySecs }

let private writeExecutableScript (dir: string) (body: string) : string =
    let path = Path.Combine(dir, "omp")
    File.WriteAllText(path, body)

    File.SetUnixFileMode(
        path,
        UnixFileMode.UserRead
        ||| UnixFileMode.UserWrite
        ||| UnixFileMode.UserExecute
        ||| UnixFileMode.GroupRead
        ||| UnixFileMode.GroupExecute
    )

    path

let private readyLine =
    """{"type":"ready","protocolVersion":1,"supportedProtocolVersions":[1,2],"maxFrameBytes":1048576,"maxReassembledFrameBytes":67108864}"""

[<Fact>]
let ``omp process start captures ready frame and can be killed`` () =
    task {
        let dir =
            Path.Combine(Path.GetTempPath(), "phos-omp-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(dir) |> ignore

        try
            let script =
                writeExecutableScript dir ("#!/usr/bin/env bash\necho '" + readyLine + "'\ncat > /dev/null\n")

            match OmpProcess.Start(ompOpts script 5, NullLogger<OmpProcess>.Instance) with
            | Error e -> failwith ("expected Ok, got " + e)
            | Ok p ->
                p.ReadyFrame |> RpcProtocol.classify |> should equal FrameKind.Ready
                p.IsDead |> should equal false
                p.Kill()
        finally
            Directory.Delete(dir, true)
    }

[<Fact>]
let ``omp process omits optional max-time ceiling`` () =
    task {
        let dir =
            Path.Combine(Path.GetTempPath(), "phos-omp-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(dir) |> ignore

        try
            let argsPath = Path.Combine(dir, "args")

            let script =
                writeExecutableScript
                    dir
                    (sprintf
                        "#!/usr/bin/env bash\nprintf '%%s\\n' \"$@\" > '%s'\necho '%s'\ncat > /dev/null\n"
                        argsPath
                        readyLine)

            let options = { ompOpts script 5 with MaxTime = "" }

            match OmpProcess.Start(options, NullLogger<OmpProcess>.Instance) with
            | Error e -> failwith ("expected Ok, got " + e)
            | Ok p ->
                let args = File.ReadAllLines argsPath |> Array.toList
                args |> List.contains "--max-time" |> should be False
                p.Kill()
        finally
            Directory.Delete(dir, true)
    }

[<Fact>]
let ``omp process start rejects non-ready frame`` () =
    task {
        let dir =
            Path.Combine(Path.GetTempPath(), "phos-omp-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(dir) |> ignore

        try
            let script =
                writeExecutableScript
                    dir
                    ("#!/usr/bin/env bash\necho '{\"type\":\"response\",\"command\":\"get_state\",\"success\":true}'\ncat > /dev/null\n")

            match OmpProcess.Start(ompOpts script 5, NullLogger<OmpProcess>.Instance) with
            | Ok _ -> failwith "expected Error for non-ready frame"
            | Error e -> Assert.Contains("expected ready frame", e)
        finally
            Directory.Delete(dir, true)
    }

[<Fact>]
let ``omp process start reports premature exit`` () =
    task {
        let dir =
            Path.Combine(Path.GetTempPath(), "phos-omp-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(dir) |> ignore

        try
            let script = writeExecutableScript dir "#!/usr/bin/env bash\nexit 3\n"

            match OmpProcess.Start(ompOpts script 5, NullLogger<OmpProcess>.Instance) with
            | Ok _ -> failwith "expected Error for premature exit"
            | Error e -> Assert.Contains("exited before ready", e)
        finally
            Directory.Delete(dir, true)
    }

[<Fact>]
let ``omp process start times out when ready frame never arrives`` () =
    task {
        let dir =
            Path.Combine(Path.GetTempPath(), "phos-omp-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(dir) |> ignore

        try
            let script = writeExecutableScript dir "#!/usr/bin/env bash\nsleep 30\n"

            match OmpProcess.Start(ompOpts script 1, NullLogger<OmpProcess>.Instance) with
            | Ok _ -> failwith "expected Error for timeout"
            | Error e -> Assert.Contains("did not become ready", e)
        finally
            Directory.Delete(dir, true)
    }

[<Fact>]
let ``worker scan loop claims and processes pending chat`` () =
    task {
        let inbox = FakeInbox()
        let sessions = FakeSessions()
        let worker = mkWorker inbox sessions

        inbox.InsertCommand
            { Origin = Telegram
              ExternalKey = None
              UserId = UserId 1L
              ChatId = ChatId 1L
              Payload = "/stop"
              Priority = 0
              Images = [] }
        |> ignore

        use cts = new CancellationTokenSource()
        let run = worker.ExecuteAsync(cts.Token)
        // Let the scan loop drain the inbox once, then stop the service.
        do! Task.Delay(500)
        cts.Cancel()
        let! _ = Task.WhenAny(run, Task.Delay(2000))

        sessions.AbortCount |> should equal 1

        inbox.Commands
        |> Seq.exists (fun c -> c.Status = Inbox.Status.Completed)
        |> should equal true
    }

// ---------------------------------------------------------------------------
// RpcTypes: Json accessors, frame parsing, classify, reassembler edge cases
// ---------------------------------------------------------------------------

[<Fact>]
let ``json accessors coerce numeric types and reject wrong types`` () =
    task {
        let obj = JsonObject()
        obj["intFromInt64"] <- JsonValue.Create(5L) :> JsonNode
        obj["int64FromInt"] <- JsonValue.Create(5) :> JsonNode
        obj["str"] <- JsonValue.Create "abc"
        obj["bool"] <- JsonValue.Create true
        obj["numAsStr"] <- JsonValue.Create 42

        Json.getInt "intFromInt64" obj |> should equal (Some 5)
        Json.getInt64 "int64FromInt" obj |> should equal (Some 5L)
        Json.getString "str" obj |> should equal (Some "abc")
        Json.getBool "bool" obj |> should equal (Some true)
        // Wrong type -> None (never throws).
        Json.getString "bool" obj |> should equal None
        Json.getBool "str" obj |> should equal None
        Json.getInt "str" obj |> should equal None
        // Missing key -> None.
        Json.getInt "missing" obj |> should equal None
        Json.getString "missing" obj |> should equal None
        Json.getObject "str" obj |> should equal None
        Json.getArray "str" obj |> should equal None

        let child = JsonObject()
        child["k"] <- 1
        obj["child"] <- (child :> JsonNode)
        Json.getObject "child" obj |> should equal (Some child)
    }

[<Fact>]
let ``try parse frame rejects empty, non-object and invalid json`` () =
    task {
        match RpcProtocol.tryParseFrame "" with
        | Error e -> e |> should equal "empty frame"
        | Ok _ -> failwith "expected empty error"

        match RpcProtocol.tryParseFrame "   " with
        | Error e -> e |> should equal "empty frame"
        | Ok _ -> failwith "expected whitespace error"

        match RpcProtocol.tryParseFrame "not json" with
        | Error _ -> ()
        | Ok _ -> failwith "expected parse error"

        match RpcProtocol.tryParseFrame "[1,2,3]" with
        | Error _ -> ()
        | Ok _ -> failwith "expected non-object error"

        match RpcProtocol.tryParseFrame """{"a":1}""" with
        | Ok obj -> Json.getInt "a" obj |> should equal (Some 1)
        | Error e -> failwith e
    }

[<Fact>]
let ``classify maps known discriminators and defaults`` () =
    task {
        let mk (t: string) =
            let o = JsonObject()
            o["type"] <- t
            o

        RpcProtocol.classify (mk "ready") |> should equal FrameKind.Ready
        RpcProtocol.classify (mk "response") |> should equal FrameKind.Response
        RpcProtocol.classify (mk "rpc_chunk") |> should equal FrameKind.RpcChunk

        RpcProtocol.classify (mk "host_tool_call")
        |> should equal FrameKind.HostToolCall

        RpcProtocol.classify (mk "host_uri_request")
        |> should equal FrameKind.HostUriRequest

        RpcProtocol.classify (mk "agent_end") |> should equal FrameKind.Event
        RpcProtocol.classify (JsonObject()) |> should equal FrameKind.Unknown
    }

let private chunkFrame (chunkId: string) (index: int) (count: int) (byteLength: int64) (data: string) : JsonObject =
    let o = JsonObject()
    o["type"] <- "rpc_chunk"
    o["chunkId"] <- chunkId
    o["index"] <- index
    o["count"] <- count
    o["byteLength"] <- byteLength
    o["data"] <- data
    o

let private b64Of (s: string) : string =
    Convert.ToBase64String(Encoding.UTF8.GetBytes s)

[<Fact>]
let ``reassembler completes single chunk and reports bytes`` () =
    task {
        let payload = """{"type":"message_update","value":7}"""
        let data = b64Of payload
        let frame = chunkFrame "c1" 0 1 (int64 (Encoding.UTF8.GetByteCount payload)) data
        let r = RpcChunkReassembler(10000L)

        match r.TryAdd frame with
        | Ok(Some obj) -> Json.getInt "value" obj |> should equal (Some 7)
        | Ok None -> failwith "expected reassembly completion on first sequence"
        | Error e -> failwith e
    }

[<Fact>]
let ``reassembler treats byteLength as the total frame size`` () =
    task {
        // Mirrors omp's RpcFrameEncoder: byteLength is the FULL reassembled
        // frame size declared identically in every chunk; chunk payloads are
        // up to 256 KiB; count = ceil(size / 256 KiB).
        let payload = String.replicate 300_000 "ab"
        let json = sprintf """{"type":"message_update","value":"%s"}""" payload
        let bytes = Encoding.UTF8.GetBytes json
        let cnt = (bytes.Length + 262_143) / 262_144
        let r = RpcChunkReassembler(1_000_000L)
        let mutable result = None

        for i in 0 .. cnt - 1 do
            let start = i * 262_144
            let len = min 262_144 (bytes.Length - start)
            let slice = bytes.[start .. start + len - 1]

            let frame =
                chunkFrame "c1" i cnt (int64 bytes.Length) (Convert.ToBase64String slice)

            match r.TryAdd frame with
            | Ok None -> ()
            | Ok(Some obj) -> result <- Some obj
            | Error e -> failwithf "unexpected error: %s" e

        match result with
        | Some obj -> Json.getString "value" obj |> should equal (Some payload)
        | None -> failwith "expected reassembly completion"
    }

[<Fact>]
let ``reassembler rejects frames missing required fields`` () =
    task {
        let r = RpcChunkReassembler(10000L)
        let frame = JsonObject()
        frame["type"] <- "rpc_chunk"
        // Missing chunkId/index/count/byteLength/data
        match r.TryAdd frame with
        | Error _ -> ()
        | Ok _ -> failwith "expected error"
    }

[<Fact>]
let ``reassembler rejects invalid index and count`` () =
    task {
        let r = RpcChunkReassembler(10000L)
        // index >= count
        match r.TryAdd(chunkFrame "c1" 2 2 0L "") with
        | Error _ -> ()
        | Ok _ -> failwith "expected index error"
        // count <= 0
        match r.TryAdd(chunkFrame "c1" 0 0 0L "") with
        | Error _ -> ()
        | Ok _ -> failwith "expected count error"
        // index < 0
        match r.TryAdd(chunkFrame "c1" -1 1 0L "") with
        | Error _ -> ()
        | Ok _ -> failwith "expected negative index error"
    }

[<Fact>]
let ``reassembler rejects byte length mismatch`` () =
    task {
        let payload = """{"a":1}"""
        let data = b64Of payload
        let r = RpcChunkReassembler(10000L)
        // Declared length is wrong.
        match r.TryAdd(chunkFrame "c1" 0 1 9999L data) with
        | Error _ -> ()
        | Ok _ -> failwith "expected byteLength error"
    }

[<Fact>]
let ``reassembler rejects exceeding the byte ceiling`` () =
    task {
        let payload = """{"a":1}"""
        let data = b64Of payload
        let actual = int64 (Encoding.UTF8.GetByteCount payload)
        let r = RpcChunkReassembler(1L)

        match r.TryAdd(chunkFrame "c1" 0 1 actual data) with
        | Error _ -> ()
        | Ok _ -> failwith "expected limit error"
    }

[<Fact>]
let ``reassembler rejects invalid utf8`` () =
    task {
        // 0xFF 0xFE is not valid UTF-8.
        let bad = Convert.ToBase64String [| 0xFFuy; 0xFEuy |]
        let r = RpcChunkReassembler(10000L)

        match r.TryAdd(chunkFrame "c1" 0 1 2L bad) with
        | Error _ -> ()
        | Ok _ -> failwith "expected utf8 error"
    }

[<Fact>]
let ``reassembler rejects non-json reassembled text`` () =
    task {
        let data = b64Of "this is not json"
        let r = RpcChunkReassembler(10000L)

        match r.TryAdd(chunkFrame "c1" 0 1 (int64 (Encoding.UTF8.GetByteCount "this is not json")) data) with
        | Error _ -> ()
        | Ok _ -> failwith "expected json parse error"
    }

[<Fact>]
let ``reassembler reassembles a two chunk sequence`` () =
    task {
        let payload = """{"type":"message_update","value":9}"""
        let bytes = Encoding.UTF8.GetBytes payload
        let half = bytes.Length / 2
        let first = bytes.[0 .. half - 1]
        let second = bytes.[half..]
        let r = RpcChunkReassembler(10000L)

        // byteLength is the FULL frame size, identical in every chunk.
        let f1 = chunkFrame "seq" 0 2 (int64 bytes.Length) (Convert.ToBase64String first)
        let f2 = chunkFrame "seq" 1 2 (int64 bytes.Length) (Convert.ToBase64String second)

        match r.TryAdd f1 with
        | Ok None -> ()
        | _ -> failwith "expected incomplete reassembly on first sequence"

        match r.TryAdd f2 with
        | Ok(Some obj) -> Json.getInt "value" obj |> should equal (Some 9)
        | Ok None -> failwith "expected reassembly completion on second sequence"
        | Error e -> failwith e
    }

// ---------------------------------------------------------------------------
// HostUriResolver error paths
// ---------------------------------------------------------------------------

[<Fact>]
let ``uri resolver rejects unsupported scheme`` () =
    task {
        let resolver =
            HostUriResolver(FakeTransport(), NullLogger<HostUriResolver>.Instance)

        let frame = JsonObject()
        frame["type"] <- "host_uri_request"
        frame["id"] <- "u1"
        frame["operation"] <- "read"
        frame["url"] <- "http://example.com/x"

        let! result = resolver.TryResolve frame

        match result with
        | Some r ->
            Json.getString "type" r |> should equal (Some "host_uri_result")
            Json.getBool "isError" r |> should equal (Some true)
        | None -> failwith "expected a host_tool error frame for flood wait"
    }

[<Fact>]
let ``uri resolver rejects malformed path and bad chat id`` () =
    task {
        let resolver =
            HostUriResolver(FakeTransport(), NullLogger<HostUriResolver>.Instance)
        // One segment only.
        let frame = JsonObject()
        frame["type"] <- "host_uri_request"
        frame["id"] <- "u2"
        frame["operation"] <- "read"
        frame["url"] <- "tg://message/onlyone"

        let! result = resolver.TryResolve frame

        match result with
        | Some r -> Json.getBool "isError" r |> should equal (Some true)
        | None -> failwith "expected error frame #1"

        // Non-numeric chat id.
        let frame2 = JsonObject()
        frame2["type"] <- "host_uri_request"
        frame2["id"] <- "u3"
        frame2["operation"] <- "read"
        frame2["url"] <- "tg://message/abc/2"

        let! result2 = resolver.TryResolve frame2

        match result2 with
        | Some r2 -> Json.getBool "isError" r2 |> should equal (Some true)
        | None -> failwith "expected error frame #2"
    }

[<Fact>]
let ``uri resolver returns error on download failure`` () =
    task {
        let transport =
            { new ITelegramTransport with
                member _.Login() =
                    task { return { BotId = 1L; Username = None } }

                member _.SendMessage(_: SendTarget) =
                    task { return Ok { RemoteMessageId = 1L } }

                member _.EditMessage (_: ChatId) (_: int64) (_: string) (_: TelegramEntity list) = Task.FromResult(())

                member _.DownloadVoice(_: VoiceRef) =
                    task { return failwith "download exploded" }

                member _.DownloadPhoto(_: PhotoRef) = task { return [||] }

                member _.SetReaction (_: ChatId) (_: int64) (_: string) = Task.FromResult(())

                member _.SetTyping(_: ChatId) = Task.FromResult(())

                member _.GetMessageSummary _ _ = task { return None }

                member _.DownloadMessagePhoto _ _ = task { return None }

                member _.GetHistory _ _ _ = task { return [] }

                member _.SendMedia(_: MediaTarget) =
                    task { return Ok { RemoteMessageId = 1L } } }

        let resolver = HostUriResolver(transport, NullLogger<HostUriResolver>.Instance)
        let frame = JsonObject()
        frame["type"] <- "host_uri_request"
        frame["id"] <- "u4"
        frame["operation"] <- "read"
        frame["url"] <- "tg://message/1/2"

        let! result = resolver.TryResolve frame

        match result with
        | Some r -> Json.getBool "isError" r |> should equal (Some true)
        | None -> failwith "expected a host_tool error frame for slowmode"
    }

// ---------------------------------------------------------------------------
// EventFormatter edge cases
// ---------------------------------------------------------------------------

[<Fact>]
let ``formatter ignores message_update without assistant event`` () =
    let ctx = { CommandId = 1L; ChatId = ChatId 1L }
    let frame = JsonObject()
    frame["type"] <- "message_update"

    let st, envs, _ = EventFormatter.onEvent ctx EventFormatter.initialState frame
    envs |> should be Empty

[<Fact>]
let ``formatter ignores non text_delta assistant event`` () =
    let ctx = { CommandId = 1L; ChatId = ChatId 1L }
    let frame = JsonObject()
    frame["type"] <- "message_update"
    let ev = JsonObject()
    ev["type"] <- "tool_use"
    frame["assistantMessageEvent"] <- (ev :> JsonNode)

    let st, envs, _ = EventFormatter.onEvent ctx EventFormatter.initialState frame
    envs |> should be Empty

[<Fact>]
let ``formatter ignores empty delta and keeps state`` () =
    let ctx = { CommandId = 1L; ChatId = ChatId 1L }
    let frame = JsonObject()
    frame["type"] <- "message_update"
    let ev = JsonObject()
    ev["type"] <- "text_delta"
    ev["delta"] <- ""
    frame["assistantMessageEvent"] <- (ev :> JsonNode)

    let st, envs, _ = EventFormatter.onEvent ctx EventFormatter.initialState frame
    envs |> should be Empty

[<Fact>]
let ``formatter appends subsequent deltas without a typing status`` () =
    let ctx = { CommandId = 1L; ChatId = ChatId 1L }

    let mkDelta (d: string) =
        let frame = JsonObject()
        frame["type"] <- "message_update"
        let ev = JsonObject()
        ev["type"] <- "text_delta"
        ev["delta"] <- d
        frame["assistantMessageEvent"] <- (ev :> JsonNode)
        frame

    let st1, envs1, _ =
        EventFormatter.onEvent ctx EventFormatter.initialState (mkDelta "hi")

    envs1 |> should be Empty

    let st2, envs2, _ = EventFormatter.onEvent ctx st1 (mkDelta " there")
    envs2 |> should be Empty
    st2.Accumulated |> should equal "hi there"

[<Fact>]
let ``formatter defaults missing isTerminal to terminal`` () =
    let ctx = { CommandId = 1L; ChatId = ChatId 1L }
    // Accumulate some text first.
    let delta = JsonObject()
    delta["type"] <- "message_update"
    let ev = JsonObject()
    ev["type"] <- "text_delta"
    ev["delta"] <- "answer"
    delta["assistantMessageEvent"] <- (ev :> JsonNode)
    let st1, _, _ = EventFormatter.onEvent ctx EventFormatter.initialState delta

    let endFrame = JsonObject()
    endFrame["type"] <- "agent_end"
    // No isTerminal key.

    let st2, envs, _ = EventFormatter.onEvent ctx st1 endFrame
    envs |> should not' (be Empty)

// ---------------------------------------------------------------------------
// SessionManager: get_state non-object + host tool no-match
// ---------------------------------------------------------------------------

[<Fact>]
let ``handle_event host tool call with unknown tool sends nothing`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ _ -> Task.FromResult(()))
        let user = UserId 1L
        let! _ = sm.EnsureRuntime user

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_9"
        frame["toolCallId"] <- "toolu_9"
        frame["toolName"] <- "no_such_tool"
        let args = JsonObject()
        frame["arguments"] <- (args :> JsonNode)

        // No exception, nothing echoed.
        do! sm.HandleEvent(user, frame)
        client.PromptCount |> should equal 0
    }

// ---------------------------------------------------------------------------
// RpcClient: definition handshake, closed pipe and write failure
// ---------------------------------------------------------------------------

[<Fact>]
let ``client registers host tools and uri schemes with the server`` () =
    task {
        use proc = startFakeServer "basic"

        do!
            withClient proc (fun client ->
                task {
                    let! toolsRes = client.SetHostToolsAsync HostTools.definitions

                    match toolsRes with
                    | Ok data ->
                        let names =
                            Json.getArray "toolNames" (data :?> JsonObject)
                            |> Option.defaultWith (fun () -> JsonArray())
                            |> Seq.map string
                            |> Seq.toList

                        names |> List.length |> should equal 14
                        Assert.Contains("tg_send_photo", names)
                        Assert.Contains("tg_send_video", names)
                        Assert.Contains("tg_send_sticker", names)
                    | Error e -> failwith e.Message

                    let! schemesRes =
                        client.SetHostUriSchemesAsync
                            [ { Scheme = "tg"
                                Description = "Telegram voice messages"
                                Writable = false
                                Immutable = false } ]

                    match schemesRes with
                    | Ok data ->
                        let schemes =
                            Json.getArray "schemes" (data :?> JsonObject)
                            |> Option.defaultWith (fun () -> JsonArray())

                        schemes |> Seq.length |> should equal 1
                    | Error e -> failwith e.Message
                })
    }

[<Fact>]
let ``send returns closed error after the child proc dies`` () =
    task {
        use proc = startFakeServer "basic"
        let client = OmpRpcClient(proc, NullLogger<OmpRpcClient>.Instance) :> IOmpRpcClient

        try
            proc.Kill(true)
            let sw = Stopwatch.StartNew()

            while not client.IsDead && sw.ElapsedMilliseconds < 5000L do
                do! Task.Delay 50

            client.IsDead |> should be True
            let! r = client.SendAsync("get_state", JsonObject() :> JsonNode)

            match r with
            | Error e -> e.Code |> should equal (Some "closed")
            | Ok _ -> failwith "expected a closed error"
        finally
            client.Dispose()
    }

[<Fact>]
let ``send resolves an error when writing to a closed pipe`` () =
    task {
        let psi = ProcessStartInfo()
        psi.FileName <- "sleep"
        psi.ArgumentList.Add("30")
        psi.RedirectStandardInput <- true
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false

        use proc = new Process()
        proc.StartInfo <- psi
        proc.Start() |> ignore
        let client = OmpRpcClient(proc, NullLogger<OmpRpcClient>.Instance) :> IOmpRpcClient

        try
            proc.StandardInput.Close()
            let! r = client.SendAsync("get_state", JsonObject() :> JsonNode)

            match r with
            | Error e -> e.Code |> should equal (Some "write")
            | Ok _ -> failwith "expected a write error"
        finally
            try
                proc.Kill(true)
            with _ ->
                ()

            client.Dispose()
    }

// ---------------------------------------------------------------------------
// HostToolExecutor: Telegram send-error mapping
// ---------------------------------------------------------------------------

[<Fact>]
let ``executor maps flood wait to an error`` () =
    task {
        let transport =
            { new ITelegramTransport with
                member _.Login() =
                    task { return { BotId = 1L; Username = None } }

                member _.SendMessage(_: SendTarget) = task { return Error(FloodWait 5) }
                member _.EditMessage (_: ChatId) (_: int64) (_: string) (_: TelegramEntity list) = Task.FromResult(())
                member _.DownloadVoice(_: VoiceRef) = task { return [||] }

                member _.DownloadPhoto(_: PhotoRef) = task { return [||] }

                member _.SetReaction (_: ChatId) (_: int64) (_: string) = Task.FromResult(())

                member _.SetTyping(_: ChatId) = Task.FromResult(())

                member _.GetMessageSummary _ _ = task { return None }

                member _.DownloadMessagePhoto _ _ = task { return None }

                member _.GetHistory _ _ _ = task { return [] }

                member _.SendMedia(_: MediaTarget) =
                    task { return Ok { RemoteMessageId = 1L } } }

        let executor =
            HostToolExecutor(
                transport,
                FakeVoiceProcessor(Ok "hi"),
                mkWorkspaces (),
                FakeScheduleRepo(),
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_fw"
        frame["toolCallId"] <- "toolu_fw"
        frame["toolName"] <- "tg_send_message"
        let args = JsonObject()
        args["chat_id"] <- 1L
        args["text"] <- "hi"
        frame["arguments"] <- (args :> JsonNode)

        let! result = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        match result with
        | Some r ->
            Json.getBool "isError" r |> should equal (Some true)
            Assert.Contains("flood wait", hostToolResultText r)
        | None -> failwith "expected a host_tool result frame for send"
    }

[<Fact>]
let ``executor maps slowmode wait to an error`` () =
    task {
        let transport =
            { new ITelegramTransport with
                member _.Login() =
                    task { return { BotId = 1L; Username = None } }

                member _.SendMessage(_: SendTarget) = task { return Error(SlowModeWait 7) }
                member _.EditMessage (_: ChatId) (_: int64) (_: string) (_: TelegramEntity list) = Task.FromResult(())
                member _.DownloadVoice(_: VoiceRef) = task { return [||] }

                member _.DownloadPhoto(_: PhotoRef) = task { return [||] }

                member _.SetReaction (_: ChatId) (_: int64) (_: string) = Task.FromResult(())

                member _.SetTyping(_: ChatId) = Task.FromResult(())

                member _.GetMessageSummary _ _ = task { return None }

                member _.DownloadMessagePhoto _ _ = task { return None }

                member _.GetHistory _ _ _ = task { return [] }

                member _.SendMedia(_: MediaTarget) =
                    task { return Ok { RemoteMessageId = 1L } } }

        let executor =
            HostToolExecutor(
                transport,
                FakeVoiceProcessor(Ok "hi"),
                mkWorkspaces (),
                FakeScheduleRepo(),
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_sm"
        frame["toolCallId"] <- "toolu_sm"
        frame["toolName"] <- "tg_send_message"
        let args = JsonObject()
        args["chat_id"] <- 1L
        args["text"] <- "hi"
        frame["arguments"] <- (args :> JsonNode)

        let! result = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        match result with
        | Some r ->
            Json.getBool "isError" r |> should equal (Some true)
            Assert.Contains("slowmode wait", hostToolResultText r)
        | None -> failwith "expected a host_tool result frame for edit"
    }

[<Fact>]
let ``schedule_add creates pending job and shows next occurrences`` () =
    task {
        let repo = FakeScheduleRepo()

        let executor =
            HostToolExecutor(
                FakeTransport(),
                FakeVoiceProcessor(Ok "hi"),
                mkWorkspaces (),
                repo,
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_add"
        frame["toolCallId"] <- "toolu_add"
        frame["toolName"] <- "schedule_add"
        let args = JsonObject()
        args["prompt"] <- "water the plants"
        args["interval_seconds"] <- 3600
        frame["arguments"] <- (args :> JsonNode)

        let! result = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        match result with
        | Some r ->
            Json.getBool "isError" r |> should equal None
            let text = hostToolResultText r
            Assert.Contains("Задание #1 создано", text)
            Assert.Contains("Ближайшие", text)
        | None -> failwith "expected a result frame for schedule_add"

        repo.InsertCount |> should equal 1
        repo.Jobs |> List.length |> should equal 1
        repo.Jobs.Head.Status |> should equal ScheduleStatus.Pending
    }

[<Fact>]
let ``schedule_add with after_seconds creates one-shot pending job`` () =
    task {
        let repo = FakeScheduleRepo()

        let executor =
            HostToolExecutor(
                FakeTransport(),
                FakeVoiceProcessor(Ok "hi"),
                mkWorkspaces (),
                repo,
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_oneshot"
        frame["toolCallId"] <- "toolu_oneshot"
        frame["toolName"] <- "schedule_add"
        let args = JsonObject()
        args["prompt"] <- "напиши"
        args["after_seconds"] <- 300
        args["timezone"] <- "UTC"
        frame["arguments"] <- (args :> JsonNode)

        let! result = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        match result with
        | Some r ->
            Json.getBool "isError" r |> should equal None
            let text = hostToolResultText r
            Assert.Contains("один раз", text)
            Assert.Contains("Сработает примерно в", text)
        | None -> failwith "expected a result frame for schedule_add one-shot"

        repo.InsertCount |> should equal 1
        repo.Jobs.Head.AfterSeconds |> should equal (Some 300)
        repo.Jobs.Head.Status |> should equal ScheduleStatus.Pending
    }

[<Fact>]
let ``schedule_add accepts future run_at and defaults timezone to host local`` () =
    task {
        let repo = FakeScheduleRepo()

        let executor =
            HostToolExecutor(
                FakeTransport(),
                FakeVoiceProcessor(Ok "hi"),
                mkWorkspaces (),
                repo,
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_calendar"
        frame["toolCallId"] <- "toolu_calendar"
        frame["toolName"] <- "schedule_add"
        let args = JsonObject()
        let value = "2099-10-02T09:00"
        args["prompt"] <- "поздравить"
        args["run_at"] <- value
        frame["arguments"] <- (args :> JsonNode)

        let! result = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        match result with
        | Some r ->
            Json.getBool "isError" r |> should equal None
            Assert.Contains("один раз", hostToolResultText r)
            Assert.Contains("Спроси у пользователя подтверждение", hostToolResultText r)
        | None -> failwith "expected a result frame for calendar schedule_add"

        let job = repo.Jobs.Head
        job.Timezone |> should equal TimeZoneInfo.Local.Id

        let expected =
            match parseRunAt TimeZoneInfo.Local.Id value with
            | Ok occurrence -> occurrence
            | Error e -> failwithf "expected local parse to succeed: %s" e

        job.RunAt |> should equal (Some expected)
        job.Status |> should equal ScheduleStatus.Pending
    }

[<Fact>]
let ``schedule_add rejects date-only past and mixed calendar modes`` () =
    task {
        let repo = FakeScheduleRepo()

        let executor =
            HostToolExecutor(
                FakeTransport(),
                FakeVoiceProcessor(Ok "hi"),
                mkWorkspaces (),
                repo,
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let call (toolCallId: string) (runAt: string) (addInterval: bool) =
            let frame = JsonObject()
            frame["type"] <- "host_tool_call"
            frame["id"] <- "host_" + toolCallId
            frame["toolCallId"] <- toolCallId
            frame["toolName"] <- "schedule_add"
            let args = JsonObject()
            args["prompt"] <- "напомни"
            args["run_at"] <- runAt

            if addInterval then
                args["interval_seconds"] <- 3600

            frame["arguments"] <- (args :> JsonNode)
            executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        let! dateOnly = call "toolu_date_only" "2099-10-02" false
        let! past = call "toolu_past" "2000-10-02T09:00" false
        let! mixed = call "toolu_mixed" "2099-10-02T09:00" true

        for result, expected in [ dateOnly, "точное время"; past, "будущ"; mixed, "ровно один" ] do
            match result with
            | Some frame ->
                Json.getBool "isError" frame |> should equal (Some true)
                Assert.Contains(expected, hostToolResultText frame)
            | None -> failwith "expected an error result frame"

        repo.InsertCount |> should equal 0
    }

[<Fact>]
let ``throwing host tool returns isError result frame`` () =
    task {
        let repo = FakeScheduleRepo()
        repo.ThrowOnInsert

        let executor =
            HostToolExecutor(
                FakeTransport(),
                FakeVoiceProcessor(Ok "hi"),
                mkWorkspaces (),
                repo,
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_throw"
        frame["toolCallId"] <- "toolu_throw"
        frame["toolName"] <- "schedule_add"
        let args = JsonObject()
        args["prompt"] <- "напиши"
        args["after_seconds"] <- 300
        args["timezone"] <- "UTC"
        frame["arguments"] <- (args :> JsonNode)

        let! result = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        match result with
        | Some r ->
            Json.getBool "isError" r |> should equal (Some true)
            Assert.Contains("внутренняя ошибка", hostToolResultText r)
        | None -> failwith "expected a result frame for throwing host tool"

        repo.InsertCount |> should equal 0
    }

[<Fact>]
let ``schedule_add rejects over quota`` () =
    task {
        let repo = FakeScheduleRepo()
        repo.SetCountActive defaultQuota.MaxJobsPerUser

        let executor =
            HostToolExecutor(
                FakeTransport(),
                FakeVoiceProcessor(Ok "hi"),
                mkWorkspaces (),
                repo,
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_quota"
        frame["toolCallId"] <- "toolu_quota"
        frame["toolName"] <- "schedule_add"
        let args = JsonObject()
        args["prompt"] <- "water"
        args["interval_seconds"] <- 3600
        frame["arguments"] <- (args :> JsonNode)

        let! result = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        match result with
        | Some r ->
            Json.getBool "isError" r |> should equal (Some true)
            Assert.Contains("достигнут лимит заданий", hostToolResultText r)
        | None -> failwith "expected a result frame for schedule_add over quota"

        repo.InsertCount |> should equal 0
    }

[<Fact>]
let ``schedule_confirm requires user origin`` () =
    task {
        let repo = FakeScheduleRepo()

        let draft: ScheduleJobDraft =
            { UserId = UserId 1L
              ChatId = ChatId 1L
              Prompt = "remind"
              CronExpr = None
              IntervalSeconds = Some 3600
              AfterSeconds = None
              RunAt = None
              Timezone = "UTC"
              Catchup = SkipMissed }

        let! job = (repo :> IScheduleJobRepository).Insert draft None

        let executor =
            HostToolExecutor(
                FakeTransport(),
                FakeVoiceProcessor(Ok "hi"),
                mkWorkspaces (),
                repo,
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        // Schedule origin is rejected.
        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_cf"
        frame["toolCallId"] <- "toolu_cf"
        frame["toolName"] <- "schedule_confirm"
        let args = JsonObject()
        args["job_id"] <- job.Id
        frame["arguments"] <- (args :> JsonNode)

        let! result = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Schedule, frame)

        match result with
        | Some r ->
            Json.getBool "isError" r |> should equal (Some true)
            Assert.Contains("подтверждение доступно только из хода пользователя", hostToolResultText r)
        | None -> failwith "expected a result frame for confirm (schedule origin)"

        // Telegram origin confirms.
        let frame2 = JsonObject()
        frame2["type"] <- "host_tool_call"
        frame2["id"] <- "host_cf2"
        frame2["toolCallId"] <- "toolu_cf2"
        frame2["toolName"] <- "schedule_confirm"
        let args2 = JsonObject()
        args2["job_id"] <- job.Id
        frame2["arguments"] <- (args2 :> JsonNode)

        let! result2 = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame2)

        match result2 with
        | Some r2 ->
            Json.getBool "isError" r2 |> should equal None
            Assert.Contains("Задание #1 активно", hostToolResultText r2)
        | None -> failwith "expected a result frame for confirm (telegram origin)"

        repo.GetJob(job.Id).Value.Status |> should equal ScheduleStatus.Active
    }

[<Fact>]
let ``schedule_list renders jobs`` () =
    task {
        let repo = FakeScheduleRepo()

        let draft1: ScheduleJobDraft =
            { UserId = UserId 1L
              ChatId = ChatId 1L
              Prompt = "pending job"
              CronExpr = None
              IntervalSeconds = Some 3600
              AfterSeconds = None
              RunAt = None
              Timezone = "UTC"
              Catchup = SkipMissed }

        let! _ = (repo :> IScheduleJobRepository).Insert draft1 None

        let draft2: ScheduleJobDraft =
            { UserId = UserId 1L
              ChatId = ChatId 1L
              Prompt = "active job"
              CronExpr = None
              IntervalSeconds = Some 3600
              AfterSeconds = None
              RunAt = None
              Timezone = "UTC"
              Catchup = SkipMissed }

        let! j2 = (repo :> IScheduleJobRepository).Insert draft2 None
        do! (repo :> IScheduleJobRepository).Confirm j2.Id

        let executor =
            HostToolExecutor(
                FakeTransport(),
                FakeVoiceProcessor(Ok "hi"),
                mkWorkspaces (),
                repo,
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_list"
        frame["toolCallId"] <- "toolu_list"
        frame["toolName"] <- "schedule_list"
        frame["arguments"] <- (JsonObject() :> JsonNode)

        let! result = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        match result with
        | Some r ->
            Json.getBool "isError" r |> should equal None
            let text = hostToolResultText r
            Assert.Contains("#1 [pending] pending job next:", text)
            Assert.Contains("#2 [active] active job next:", text)
        | None -> failwith "expected a result frame for schedule_list"
    }

[<Fact>]
let ``schedule_list shows one-shot marker`` () =
    task {
        let repo = FakeScheduleRepo()

        let draft: ScheduleJobDraft =
            { UserId = UserId 1L
              ChatId = ChatId 1L
              Prompt = "one-shot job"
              CronExpr = None
              IntervalSeconds = None
              AfterSeconds = Some 300
              RunAt = None
              Timezone = "UTC"
              Catchup = SkipMissed }

        let! _ = (repo :> IScheduleJobRepository).Insert draft None

        let executor =
            HostToolExecutor(
                FakeTransport(),
                FakeVoiceProcessor(Ok "hi"),
                mkWorkspaces (),
                repo,
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_list_oneshot"
        frame["toolCallId"] <- "toolu_list_oneshot"
        frame["toolName"] <- "schedule_list"
        frame["arguments"] <- (JsonObject() :> JsonNode)

        let! result = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        match result with
        | Some r ->
            Json.getBool "isError" r |> should equal None
            let text = hostToolResultText r
            Assert.Contains("#1 [pending (разово)] one-shot job next:", text)
        | None -> failwith "expected a result frame for schedule_list one-shot"
    }

[<Fact>]
let ``schedule_pause resume remove run_now transitions`` () =
    task {
        let repo = FakeScheduleRepo()

        let draft: ScheduleJobDraft =
            { UserId = UserId 1L
              ChatId = ChatId 1L
              Prompt = "job"
              CronExpr = None
              IntervalSeconds = Some 3600
              AfterSeconds = None
              RunAt = None
              Timezone = "UTC"
              Catchup = SkipMissed }

        let! job = (repo :> IScheduleJobRepository).Insert draft None
        do! (repo :> IScheduleJobRepository).Confirm job.Id

        let executor =
            HostToolExecutor(
                FakeTransport(),
                FakeVoiceProcessor(Ok "hi"),
                mkWorkspaces (),
                repo,
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let call (toolName: string) (toolCallId: string) : Task<JsonObject option> =
            let frame = JsonObject()
            frame["type"] <- "host_tool_call"
            frame["id"] <- "host_" + toolName
            frame["toolCallId"] <- toolCallId
            frame["toolName"] <- toolName
            let args = JsonObject()
            args["job_id"] <- job.Id
            frame["arguments"] <- (args :> JsonNode)
            executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        let! r1 = call "schedule_pause" "toolu_pause"
        r1 |> should not' (be None)
        repo.GetJob(job.Id).Value.Status |> should equal ScheduleStatus.Paused

        let! r2 = call "schedule_resume" "toolu_resume"
        r2 |> should not' (be None)
        repo.GetJob(job.Id).Value.Status |> should equal ScheduleStatus.Active

        let! r3 = call "schedule_run_now" "toolu_run"
        r3 |> should not' (be None)
        repo.GetJob(job.Id).Value.NextRun |> should not' (be None)

        let! r4 = call "schedule_remove" "toolu_remove"
        r4 |> should not' (be None)
        repo.GetJob(job.Id) |> should equal None
    }

[<Fact>]
let ``schedule_add idempotent via toolCallId`` () =
    task {
        let repo = FakeScheduleRepo()

        let draft: ScheduleJobDraft =
            { UserId = UserId 1L
              ChatId = ChatId 1L
              Prompt = "job"
              CronExpr = None
              IntervalSeconds = Some 3600
              AfterSeconds = None
              RunAt = None
              Timezone = "UTC"
              Catchup = SkipMissed }

        let! _ = (repo :> IScheduleJobRepository).Insert draft (Some "toolu_dup")

        let executor =
            HostToolExecutor(
                FakeTransport(),
                FakeVoiceProcessor(Ok "hi"),
                mkWorkspaces (),
                repo,
                defaultQuota,
                NullLogger<HostToolExecutor>.Instance
            )

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_dup"
        frame["toolCallId"] <- "toolu_dup"
        frame["toolName"] <- "schedule_add"
        let args = JsonObject()
        args["prompt"] <- "job"
        args["interval_seconds"] <- 3600
        frame["arguments"] <- (args :> JsonNode)

        let! result = executor.TryExecute(UserId 1L, Some(ChatId 1L), Some Telegram, frame)

        match result with
        | Some r ->
            Json.getBool "isError" r |> should equal None
            Assert.Contains("уже создано", hostToolResultText r)
        | None -> failwith "expected a result frame for schedule_add idempotent"

        repo.InsertCount |> should equal 1
    }

// ---------------------------------------------------------------------------
// SessionManager: interface routing + runtime edge branches
// ---------------------------------------------------------------------------

[<Fact>]
let ``session manager interface routes every operation`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ _ -> Task.FromResult(()))
        let ism = sm :> IOmpSessionManager
        let user = UserId 1L

        let! _ = ism.EnsureRuntime user
        ism.IsRuntimeAlive user |> should be True
        let! _ = ism.Prompt(user, mkCommand 1L 1L "hi")
        do! ism.Abort user

        let frame = JsonObject()
        frame["type"] <- "agent_start"
        do! ism.HandleEvent(user, frame)

        ism.IdleTimeoutCheck(DateTimeOffset.UtcNow)
        ism.Shutdown()
        client.IsDisposed |> should be True
    }

[<Fact>]
let ``prompt surfaces an ensure runtime error`` () =
    task {
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Error "no omp"
        let sm = scriptedSm spawn client (fun _ _ -> Task.FromResult(()))

        let! r = sm.Prompt(UserId 1L, mkCommand 1L 1L "hi")

        match r with
        | Error e -> e |> should equal "no omp"
        | Ok() -> failwith "expected an ensure runtime error"
    }

[<Fact>]
let ``abort with runtime but no client is a no-op`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ _ -> Task.FromResult(()))
        let user = UserId 1L

        let! _ = sm.EnsureRuntime user
        fakeProc.RaiseExit()
        do! sm.Abort user
        client.AbortCount |> should equal 0
    }

[<Fact>]
let ``get_state non-object is tolerated`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        client.GetStateResult <- Ok(JsonValue.Create(5) :> JsonNode)
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ _ -> Task.FromResult(()))

        let! r = sm.EnsureRuntime(UserId 1L)

        match r with
        | Ok() -> ()
        | Error e -> failwith e

        sm.IsRuntimeAlive(UserId 1L) |> should be True
    }

[<Fact>]
let ``ensure runtime is idempotent when already running`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ _ -> Task.FromResult(()))
        let user = UserId 1L

        let! _ = sm.EnsureRuntime user
        let! r2 = sm.EnsureRuntime user

        match r2 with
        | Ok() -> ()
        | Error e -> failwith e
    }

[<Fact>]
let ``shutdown with no client or process is a no-op`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ _ -> Task.FromResult(()))
        let user = UserId 1L

        let! _ = sm.EnsureRuntime user
        fakeProc.RaiseExit()
        sm.Shutdown()
        client.IsDisposed |> should be False
    }

// ---------------------------------------------------------------------------
// OmpProcess: resume/extra flags handshake and invalid ready frame
// ---------------------------------------------------------------------------

[<Fact>]
let ``omp process start passes resume and extra flags`` () =
    task {
        let dir =
            Path.Combine(Path.GetTempPath(), "phos-omp-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(dir) |> ignore

        try
            let script =
                writeExecutableScript dir ("#!/usr/bin/env bash\necho '" + readyLine + "'\ncat > /dev/null\n")

            let opts =
                { (ompOpts script 5) with
                    SessionResume = Some "sess-1"
                    ExtraFlags = [ "--debug" ] }

            match OmpProcess.Start(opts, NullLogger<OmpProcess>.Instance) with
            | Ok p ->
                p.ReadyFrame |> RpcProtocol.classify |> should equal FrameKind.Ready
                p.Kill()
            | Error e -> failwith ("expected Ok, got " + e)
        finally
            Directory.Delete(dir, true)
    }

[<Fact>]
let ``omp process start rejects invalid ready json`` () =
    task {
        let dir =
            Path.Combine(Path.GetTempPath(), "phos-omp-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(dir) |> ignore

        try
            let script =
                writeExecutableScript dir "#!/usr/bin/env bash\necho 'not json'\ncat > /dev/null\n"

            match OmpProcess.Start(ompOpts script 5, NullLogger<OmpProcess>.Instance) with
            | Ok _ -> failwith "expected Error for invalid ready json"
            | Error e -> Assert.Contains("invalid JSON frame", e)
        finally
            Directory.Delete(dir, true)
    }

// ---------------------------------------------------------------------------
// RpcChunkReassembler: index gap after a started sequence
// ---------------------------------------------------------------------------

[<Fact>]
let ``reassembler rejects index gap after first chunk`` () =
    let payload = """{"type":"message_update","value":1}"""
    let bytes = Encoding.UTF8.GetBytes payload
    let third = bytes.Length / 3
    let first = bytes.[0 .. third - 1]
    let r = RpcChunkReassembler(10000L)
    let f0 = chunkFrame "gap" 0 3 (int64 first.Length) (Convert.ToBase64String first)

    match r.TryAdd f0 with
    | Ok None -> ()
    | _ -> failwith "expected incomplete reassembly on second sequence"

    // Skip index 1 and jump to index 2: an out-of-order gap.
    let f2 = chunkFrame "gap" 2 3 (int64 first.Length) (Convert.ToBase64String first)

    match r.TryAdd f2 with
    | Error _ -> ()
    | Ok _ -> failwith "expected an out-of-order gap error"

// ---------------------------------------------------------------------------
// OmpWorker: scan loop resilience + WakeChannel
// ---------------------------------------------------------------------------

[<Fact>]
let ``worker scan loop survives an inbox exception`` () =
    task {
        let inbox = FakeInbox()
        inbox.ThrowOnListPending <- true
        let sessions = FakeSessions()
        let worker = mkWorker inbox sessions

        use cts = new CancellationTokenSource()
        let run = worker.ExecuteAsync(cts.Token)
        // Let the scan loop run once (ListPendingChatIds throws, is caught),
        // then stop the service.
        do! Task.Delay(300)
        cts.Cancel()
        let! _ = Task.WhenAny(run, Task.Delay(2000))

        sessions.PromptCalls |> should equal 0
    }

[<Fact>]
let ``wake channel wakes a waiter`` () =
    task {
        let wake = WakeChannel()
        let wait = wake.WaitAsync(CancellationToken.None)
        do! Task.Delay 50
        wake.Wake()
        do! wait
    }

// ---------------------------------------------------------------------------
// Retry-aware provider failure (terminal agent_end classification)
// ---------------------------------------------------------------------------

let private mkErrorAgentEnd (isTerminal: bool) (errorMessage: string option) : JsonObject =
    let frame = JsonObject()
    frame["type"] <- "agent_end"
    frame["isTerminal"] <- isTerminal

    let m = JsonObject()
    m["role"] <- "assistant"
    m["stopReason"] <- "error"
    errorMessage |> Option.iter (fun e -> m["errorMessage"] <- e)

    let arr = JsonArray()
    arr.Add m
    frame["messages"] <- arr
    frame

[<Fact>]
let ``terminal provider error with no text emits nothing and classifies failure`` () =
    task {
        let ctx = { CommandId = 1L; ChatId = ChatId 5L }
        let frame = mkErrorAgentEnd true None

        let _, envelopes, outcome =
            EventFormatter.onEvent ctx EventFormatter.initialState frame

        envelopes |> should be Empty

        outcome
        |> should equal (Some(TurnOutcome.ProviderFailure "unknown provider error"))
    }

[<Fact>]
let ``terminal provider error suppresses the buffered partial text`` () =
    task {
        let ctx = { CommandId = 1L; ChatId = ChatId 5L }

        let delta = JsonObject()
        delta["type"] <- "message_update"

        let ev = JsonObject()
        ev["type"] <- "text_delta"
        ev["delta"] <- "partial answer"
        delta["assistantMessageEvent"] <- ev

        let st1, _, _ = EventFormatter.onEvent ctx EventFormatter.initialState delta
        st1.Accumulated |> should equal "partial answer"

        let frame = mkErrorAgentEnd true (Some "provider exploded")
        let st2, envelopes, outcome = EventFormatter.onEvent ctx st1 frame

        // The failed turn's partial output is never delivered as a success.
        envelopes |> should be Empty
        st2.Accumulated |> should equal ""
        outcome |> should equal (Some(TurnOutcome.ProviderFailure "provider exploded"))
    }

[<Fact>]
let ``aborted terminal agent_end delivers partial text and classifies abort`` () =
    task {
        let ctx = { CommandId = 1L; ChatId = ChatId 5L }

        let frame = JsonObject()
        frame["type"] <- "agent_end"
        frame["isTerminal"] <- true
        frame["stopReason"] <- "aborted"

        let m = JsonObject()
        m["role"] <- "assistant"
        m["stopReason"] <- "aborted"

        let arr = JsonArray()
        arr.Add m
        frame["messages"] <- arr

        let st =
            { Accumulated = "partial"
              MediaCount = 0 }

        let _, envelopes, outcome = EventFormatter.onEvent ctx st frame

        // Existing /stop behavior: partial text still delivered.
        envelopes |> should not' (be Empty)
        outcome |> should equal (Some TurnOutcome.Aborted)
    }

[<Fact>]
let ``terminal provider error finalizes with failure, no re-prompt, runtime preserved`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let outcomes = ResizeArray<int64 * TurnOutcome>()

        let turnEnded (cmdId: int64) (o: TurnOutcome) : Task<unit> = task { outcomes.Add(cmdId, o) }

        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client turnEnded
        let user = UserId 1L

        let! _ = sm.Prompt(user, mkCommand 1L 1L "hi")
        do! sm.HandleEvent(user, mkErrorAgentEnd true (Some "provider exploded"))

        outcomes.Count |> should equal 1
        fst outcomes.[0] |> should equal 1L

        snd outcomes.[0]
        |> should equal (TurnOutcome.ProviderFailure "provider exploded")

        // No Phos-side retry: the failed command is not re-prompted...
        client.PromptCount |> should equal 1

        // ...and the healthy OMP process/session is preserved.
        fakeProc.Killed |> should be False
        sm.IsRuntimeAlive user |> should be True

        // A new prompt still works on the same runtime.
        let! r = sm.Prompt(user, mkCommand 2L 1L "again")

        match r with
        | Ok() -> ()
        | Error e -> failwith e

        client.PromptCount |> should equal 2
    }

[<Fact>]
let ``terminal provider error drains the next queued command without requeueing the failed one`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let outcomes = ResizeArray<int64 * TurnOutcome>()

        let turnEnded (cmdId: int64) (o: TurnOutcome) : Task<unit> = task { outcomes.Add(cmdId, o) }

        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client turnEnded
        let user = UserId 1L

        let! _ = sm.Prompt(user, mkCommand 1L 1L "one")
        let! _ = sm.Prompt(user, mkCommand 2L 1L "two")
        client.PromptCount |> should equal 1

        do! sm.HandleEvent(user, mkErrorAgentEnd true (Some "boom"))

        // Failed command finalized exactly once as a provider failure...
        outcomes.Count |> should equal 1
        snd outcomes.[0] |> should equal (TurnOutcome.ProviderFailure "boom")

        // ...and only the NEXT queued command was prompted (no retry of cmd 1).
        client.PromptCount |> should equal 2
    }

[<Fact>]
let ``auto retry events and non terminal agent_end never finalize or notify`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let outcomes = ResizeArray<int64 * TurnOutcome>()

        let turnEnded (cmdId: int64) (o: TurnOutcome) : Task<unit> = task { outcomes.Add(cmdId, o) }

        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client turnEnded
        let user = UserId 1L

        let! _ = sm.Prompt(user, mkCommand 1L 1L "hi")

        // OMP's own retry cycle: intermediate boundaries must not finalize.
        let retryStart = JsonObject()
        retryStart["type"] <- "auto_retry_start"
        do! sm.HandleEvent(user, retryStart)

        let fallback = JsonObject()
        fallback["type"] <- "retry_fallback_applied"
        do! sm.HandleEvent(user, fallback)

        // An intermediate error boundary is still non-terminal.
        do! sm.HandleEvent(user, mkErrorAgentEnd false (Some "attempt 1 failed"))

        let retryEnd = JsonObject()
        retryEnd["type"] <- "auto_retry_end"
        do! sm.HandleEvent(user, retryEnd)

        outcomes.Count |> should equal 0

        // Only the exhausted terminal agent_end finalizes.
        do! sm.HandleEvent(user, mkErrorAgentEnd true (Some "retries exhausted"))

        outcomes.Count |> should equal 1

        snd outcomes.[0]
        |> should equal (TurnOutcome.ProviderFailure "retries exhausted")
    }

[<Fact>]
let ``late same-id rpc failure finalizes the turn without a duplicate prompt`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let outcomes = ResizeArray<int64 * TurnOutcome>()

        let turnEnded (cmdId: int64) (o: TurnOutcome) : Task<unit> = task { outcomes.Add(cmdId, o) }

        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client turnEnded
        let user = UserId 1L

        let! _ = sm.Prompt(user, mkCommand 1L 1L "hi")

        // The prompt id is remembered for late same-id correlation.
        client.PromptIds |> should equal [ Some "prompt_1" ]
        client.PromptCount |> should equal 1

        client.RaiseLateFailure(
            "prompt_1",
            { Command = "prompt"
              Code = Some "schedule"
              Message = "late boom" }
        )

        outcomes.Count |> should equal 1
        fst outcomes.[0] |> should equal 1L
        snd outcomes.[0] |> should equal (TurnOutcome.ProviderFailure "late boom")

        // No duplicate prompt was issued for the failed turn.
        client.PromptCount |> should equal 1
    }

[<Fact>]
let ``late rpc failure for a foreign id is ignored`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let outcomes = ResizeArray<int64 * TurnOutcome>()

        let turnEnded (cmdId: int64) (o: TurnOutcome) : Task<unit> = task { outcomes.Add(cmdId, o) }

        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client turnEnded
        let user = UserId 1L

        let! _ = sm.Prompt(user, mkCommand 1L 1L "hi")

        client.RaiseLateFailure(
            "prompt_999",
            { Command = "prompt"
              Code = None
              Message = "not ours" }
        )

        outcomes.Count |> should equal 0

        // The turn still finalizes normally afterwards.
        do! sm.HandleEvent(user, mkErrorAgentEnd false (Some "ignored"))

        let terminal = JsonObject()
        terminal["type"] <- "agent_end"
        terminal["isTerminal"] <- true
        do! sm.HandleEvent(user, terminal)

        outcomes.Count |> should equal 1
        snd outcomes.[0] |> should equal TurnOutcome.Completed
    }

[<Fact>]
let ``process exit mid turn parks the command for review without replay`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let outcomes = ResizeArray<int64 * TurnOutcome>()

        let turnEnded (cmdId: int64) (o: TurnOutcome) : Task<unit> = task { outcomes.Add(cmdId, o) }

        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client turnEnded
        let user = UserId 1L

        let! _ = sm.Prompt(user, mkCommand 1L 1L "hi")
        fakeProc.RaiseExit()
        do! Task.Delay 100

        // Unknown outcome (host tools may have run): needs_review, not auto-retry.
        outcomes.Count |> should equal 1
        snd outcomes.[0] |> should equal TurnOutcome.NeedsReview
        client.PromptCount |> should equal 1
    }

[<Fact>]
let ``idle timeout on an in-flight turn parks the command for review`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let outcomes = ResizeArray<int64 * TurnOutcome>()

        let turnEnded (cmdId: int64) (o: TurnOutcome) : Task<unit> = task { outcomes.Add(cmdId, o) }

        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let createClient (_: Process) (_: JsonObject) : IOmpRpcClient = client :> IOmpRpcClient
        let mutable currentTime = DateTimeOffset.UtcNow
        let sm = createSessionManager spawn createClient turnEnded (fun () -> currentTime)
        let user = UserId 1L

        let! _ = sm.Prompt(user, mkCommand 1L 1L "hi")

        // No events for the whole idle window: the turn is wedged (generous
        // grace exhausted) -> needs_review, never a premature kill of a
        // healthy turn that is still streaming events.
        currentTime <- currentTime.AddMinutes 45.0
        sm.IdleTimeoutCheck currentTime
        do! Task.Delay 100

        outcomes.Count |> should equal 1
        snd outcomes.[0] |> should equal TurnOutcome.NeedsReview
    }

// ---------------------------------------------------------------------------
// Durable command finalization (TurnFinalization)
// ---------------------------------------------------------------------------

let private runningCommand () : Task<FakeInbox * FakeOutbox * int64> =
    task {
        let inbox = FakeInbox()
        let outbox = FakeOutbox()

        let env: CommandEnvelope =
            { Origin = Telegram
              ExternalKey = None
              UserId = UserId 1L
              ChatId = ChatId 9L
              Payload = "hi"
              Priority = 0
              Images = [] }

        let ib = inbox :> ICommandInbox
        let! id = ib.Insert env

        let lease =
            { Until = DateTimeOffset.UtcNow.AddSeconds 60.0
              HeartbeatAt = DateTimeOffset.UtcNow }

        let! _ = ib.ClaimNextForChat (ChatId 9L) lease
        do! ib.MarkStarted id
        return (inbox, outbox, id)
    }

[<Fact>]
let ``finalization of a provider failure is durable, single-notice and not requeued`` () =
    task {
        let! inbox, outbox, id = runningCommand ()
        let logger = NullLogger.Instance

        do!
            TurnFinalization.apply
                (inbox :> ICommandInbox)
                (outbox :> IMessageOutbox)
                logger
                id
                (TurnOutcome.ProviderFailure "boom")

        // Durable failed terminal state (attempts incremented), NOT pending.
        let! c = inbox.GetById id
        c.Value.Status |> should equal Inbox.Status.Failed

        // Exactly one generic Telegram error through the outbox.
        outbox.Entries.Count |> should equal 1
        outbox.Entries.[0].ChatId |> should equal (ChatId 9L)
        outbox.Entries.[0].CommandId |> should equal id

        outbox.Entries.[0].Payload
        |> should equal TurnFinalization.ProviderFailureNotice

        // The worker scan must not requeue it: nothing claimable, and lease
        // expiry does not revive a failed command.
        let l =
            { Until = DateTimeOffset.UtcNow.AddSeconds 60.0
              HeartbeatAt = DateTimeOffset.UtcNow }

        let! claimed = (inbox :> ICommandInbox).ClaimNextForChat (ChatId 9L) l
        claimed |> should equal None
        let! _ = (inbox :> ICommandInbox).ExpireLeases(DateTimeOffset.UtcNow.AddHours 1.0)
        let! c2 = inbox.GetById id
        c2.Value.Status |> should equal Inbox.Status.Failed
    }

[<Fact>]
let ``provider failure finalization is idempotent and cannot revert completion`` () =
    task {
        let! inbox, outbox, id = runningCommand ()
        let logger = NullLogger.Instance

        do!
            TurnFinalization.apply
                (inbox :> ICommandInbox)
                (outbox :> IMessageOutbox)
                logger
                id
                (TurnOutcome.ProviderFailure "first")

        do!
            TurnFinalization.apply
                (inbox :> ICommandInbox)
                (outbox :> IMessageOutbox)
                logger
                id
                (TurnOutcome.ProviderFailure "duplicate")

        let! failed = inbox.GetById id
        failed.Value.Status |> should equal Inbox.Status.Failed
        failed.Value.Attempts |> should equal 1
        outbox.Entries.Count |> should equal 1

        let! inbox2, outbox2, id2 = runningCommand ()
        do! (inbox2 :> ICommandInbox).MarkCompleted id2

        do!
            TurnFinalization.apply
                (inbox2 :> ICommandInbox)
                (outbox2 :> IMessageOutbox)
                logger
                id2
                (TurnOutcome.ProviderFailure "late")

        let! completed = inbox2.GetById id2
        completed.Value.Status |> should equal Inbox.Status.Completed
        outbox2.Entries.Count |> should equal 0
    }

[<Fact>]
let ``finalization of normal and abort outcomes completes without outbox error`` () =
    task {
        for outcome in [ TurnOutcome.Completed; TurnOutcome.Aborted ] do
            let! inbox, outbox, id = runningCommand ()
            let logger = NullLogger.Instance

            do! TurnFinalization.apply (inbox :> ICommandInbox) (outbox :> IMessageOutbox) logger id outcome

            let! c = inbox.GetById id
            c.Value.Status |> should equal Inbox.Status.Completed
            outbox.Entries.Count |> should equal 0
    }

[<Fact>]
let ``finalization of an uncertain outcome parks the command for review`` () =
    task {
        let! inbox, outbox, id = runningCommand ()
        let logger = NullLogger.Instance

        do! TurnFinalization.apply (inbox :> ICommandInbox) (outbox :> IMessageOutbox) logger id TurnOutcome.NeedsReview

        let! c = inbox.GetById id
        c.Value.Status |> should equal Inbox.Status.NeedsReview
        outbox.Entries.Count |> should equal 1

        outbox.Entries.[0].Payload |> should equal TurnFinalization.NeedsReviewNotice
    }
