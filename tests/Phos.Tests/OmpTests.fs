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

let private deleteDir (dir: string) =
    try
        if Directory.Exists dir then
            Directory.Delete(dir, true)
    with _ ->
        ()

let private mkCommand (id: int64) (chatId: int64) (payload: string) : Command =
    { Id = id
      Envelope =
        { Origin = Telegram
          ExternalKey = None
          UserId = UserId 1L
          ChatId = ChatId chatId
          Payload = payload
          Priority = 0 }
      Status = Inbox.Status.Pending
      Attempts = 0
      MaxAttempts = 5
      LeaseUntil = None
      HeartbeatAt = None }

// ---------------------------------------------------------------------------
// Fake transport / voice / client / process
// ---------------------------------------------------------------------------

type FakeTransport(?voiceBytes: byte[]) =
    let mutable sendCount = 0
    let mutable editCount = 0
    member _.SendCount = sendCount
    member _.EditCount = editCount

    interface ITelegramTransport with
        member _.Login() =
            task { return { BotId = 1L; Username = Some "bot" } }

        member _.SendMessage(_: SendTarget) =
            task {
                sendCount <- sendCount + 1
                return Ok { RemoteMessageId = 1L }
            }

        member _.EditMessage (_: ChatId) (_: int64) (_: string) (_: TelegramEntity list) =
            task {
                editCount <- editCount + 1
                return ()
            }

        member _.DownloadVoice(_: VoiceRef) =
            task { return defaultArg voiceBytes [||] }

type FakeVoiceProcessor(result: Result<string, string>) =
    interface IVoiceProcessor with
        member _.ProcessAsync(_: VoiceRef) = task { return result }

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
        member _.IsDead = false

        member _.SendAsync(command: string, payload: JsonNode, ?id: string) : Task<Result<JsonNode, RpcError>> =
            task {
                match command with
                | "get_state" -> return Ok(stateObj () :> JsonNode)
                | _ -> return Ok(JsonObject() :> JsonNode)
            }

        member _.SendRawAsync(_: JsonObject) : Task<unit> = Task.FromResult(())

        member _.PromptAsync(message: string, ?streamingBehavior: string) : Task<Result<JsonNode, RpcError>> =
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

/// RPC client whose per-command results can be scripted, so error paths in the
/// `SessionManager` (get_state / set_host_tools / set_host_uri_schemes / prompt
/// failures) can be exercised deterministically.
type ScriptedRpcClient() =
    let eventReceived = Event<JsonObject>()
    let parseError = Event<string>()
    let mutable getStateResult: Result<JsonNode, RpcError> = Ok(JsonObject())
    let mutable setToolsResult: Result<JsonNode, RpcError> = Ok(JsonObject())
    let mutable setUrisResult: Result<JsonNode, RpcError> = Ok(JsonObject())
    let mutable promptResult: Result<JsonNode, RpcError> = Ok(JsonObject())
    let mutable promptCount = 0
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
    member _.AbortCount = abortCount
    member _.IsDisposed = disposed
    member _.RaiseEvent(frame: JsonObject) = eventReceived.Trigger frame

    interface IOmpRpcClient with
        member _.EventReceived = eventReceived.Publish
        member _.ParseError = parseError.Publish
        member _.IsDead = false

        member _.SendAsync(command: string, _: JsonNode, ?id: string) : Task<Result<JsonNode, RpcError>> =
            match command with
            | "get_state" -> Task.FromResult(getStateResult)
            | "set_host_tools" -> Task.FromResult(setToolsResult)
            | "set_host_uri_schemes" -> Task.FromResult(setUrisResult)
            | _ -> Task.FromResult(Ok(JsonObject() :> JsonNode))

        member _.SendRawAsync(_: JsonObject) : Task<unit> = Task.FromResult(())

        member _.PromptAsync(_: string, ?streamingBehavior: string) : Task<Result<JsonNode, RpcError>> =
            promptCount <- promptCount + 1
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
    chunk0["byteLength"] <- 5
    chunk0["data"] <- Convert.ToBase64String(Encoding.UTF8.GetBytes "hello")

    match r.TryAdd chunk0 with
    | Ok None -> ()
    | _ -> failwith "expected Ok None for first chunk"

    let chunk1 = JsonObject()
    chunk1["chunkId"] <- "rpc-1"
    chunk1["index"] <- 1
    chunk1["count"] <- 2
    chunk1["byteLength"] <- 10
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
            HostToolExecutor(transport, voice, NullLogger<HostToolExecutor>.Instance)

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_1"
        frame["toolCallId"] <- "toolu_1"
        frame["toolName"] <- "tg_send_message"
        let args = JsonObject()
        args["chat_id"] <- 1L
        args["text"] <- "hi"
        frame["arguments"] <- (args :> JsonNode)

        let! result = executor.TryExecute frame

        match result with
        | Some r ->
            Json.getString "type" r |> should equal (Some "host_tool_result")
            Json.getString "id" r |> should equal (Some "host_1")
            transport.SendCount |> should equal 1
        | None -> failwith "expected a result frame #1"

        // Replaying the same toolCallId must not repeat the side effect.
        let! result2 = executor.TryExecute frame

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
            HostToolExecutor(FakeTransport(), FakeVoiceProcessor(Ok "hi"), NullLogger<HostToolExecutor>.Instance)

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_2"
        frame["toolCallId"] <- "toolu_2"
        frame["toolName"] <- "nope"
        frame["arguments"] <- (JsonObject() :> JsonNode)

        let! result = executor.TryExecute frame

        match result with
        | Some r -> Json.getBool "isError" r |> should equal (Some true)
        | None -> failwith "expected a result frame #3"
    }

[<Fact>]
let ``executor returns none for non tool frame`` () =
    task {
        let executor =
            HostToolExecutor(FakeTransport(), FakeVoiceProcessor(Ok "hi"), NullLogger<HostToolExecutor>.Instance)

        let frame = JsonObject()
        frame["type"] <- "agent_start"
        let! result = executor.TryExecute frame
        result |> should equal None
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

// ---------------------------------------------------------------------------
// EventFormatter
// ---------------------------------------------------------------------------

[<Fact>]
let ``first text delta emits typing status once`` () =
    let ctx = { CommandId = 1L; ChatId = ChatId 5L }
    let frame = JsonObject()
    frame["type"] <- "message_update"
    let ev = JsonObject()
    ev["type"] <- "text_delta"
    ev["delta"] <- "Hello"
    frame["assistantMessageEvent"] <- (ev :> JsonNode)
    let st, envelopes = EventFormatter.onEvent ctx EventFormatter.initialState frame
    envelopes.Length |> should equal 1
    envelopes.[0].Payload |> should equal "…"
    envelopes.[0].ChunkIndex |> should equal -1
    st.Started |> should be True
    st.Accumulated |> should equal "Hello"

    let frame2 = JsonObject()
    frame2["type"] <- "message_update"
    let ev2 = JsonObject()
    ev2["type"] <- "text_delta"
    ev2["delta"] <- " world"
    frame2["assistantMessageEvent"] <- (ev2 :> JsonNode)
    let _, env2 = EventFormatter.onEvent ctx st frame2
    env2 |> should be Empty

[<Fact>]
let ``terminal agent_end chunks accumulated text`` () =
    let ctx = { CommandId = 1L; ChatId = ChatId 5L }
    let text = String.replicate 5000 "a"

    let st =
        { Accumulated = text
          Chunked = []
          Started = true }

    let frame = JsonObject()
    frame["type"] <- "agent_end"
    frame["isTerminal"] <- true
    let st2, envelopes = EventFormatter.onEvent ctx st frame
    envelopes.Length |> should be (greaterThan 1)
    envelopes |> List.forall (fun e -> e.Payload.Length <= 4096) |> should be True
    let joined = envelopes |> List.map (fun e -> e.Payload) |> String.concat ""
    joined |> should equal text
    st2.Accumulated |> should equal ""

[<Fact>]
let ``non terminal agent_end emits nothing`` () =
    let ctx = { CommandId = 1L; ChatId = ChatId 5L }

    let st =
        { Accumulated = "partial"
          Chunked = []
          Started = true }

    let frame = JsonObject()
    frame["type"] <- "agent_end"
    frame["isTerminal"] <- false
    let _, envelopes = EventFormatter.onEvent ctx st frame
    envelopes |> should be Empty

[<Fact>]
let ``terminal agent_end with empty text emits nothing`` () =
    let ctx = { CommandId = 1L; ChatId = ChatId 5L }

    let st =
        { Accumulated = ""
          Chunked = []
          Started = false }

    let frame = JsonObject()
    frame["type"] <- "agent_end"
    frame["isTerminal"] <- true
    let _, envelopes = EventFormatter.onEvent ctx st frame
    envelopes |> should be Empty

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

        File.ReadAllText(Path.Combine(wsDir, ".omp", "APPEND_SYSTEM.md"))
        |> should equal "CUSTOM PERSONA"
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

// ---------------------------------------------------------------------------
// SessionManager
// ---------------------------------------------------------------------------

let private createSessionManager
    (spawnProcess: OmpProcessOptions -> Result<IOmpProcess, string>)
    (createClient: Process -> JsonObject -> IOmpRpcClient)
    (turnEnded: int64 -> Task<unit>)
    (nowFn: unit -> DateTimeOffset)
    : SessionManager =
    let tmp = tempDir ()
    let profile = ProfileManager(Path.Combine(tmp, "omp"))
    profile.EnsureProfileWith("phos", "models: {}", "config: {}") |> ignore
    let workspaces = WorkspaceManager(Path.Combine(tmp, "ws"))
    let transport = FakeTransport()
    let voice = FakeVoiceProcessor(Ok "hi")

    let hostTools =
        HostToolExecutor(transport, voice, NullLogger<HostToolExecutor>.Instance)

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
        let turnEnded (_: int64) : Task<unit> = Task.FromResult(())
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
        let turnEnded (_: int64) : Task<unit> = Task.FromResult(())
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
        let turnEnded (_: int64) : Task<unit> = Task.FromResult(())
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
        let turnEnded (_: int64) : Task<unit> = Task.FromResult(())
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

        let turnEnded (cmdId: int64) : Task<unit> =
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

        let turnEnded (cmdId: int64) : Task<unit> =
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
    (turnEnded: int64 -> Task<unit>)
    : SessionManager =
    let createClient (_: Process) (_: JsonObject) : IOmpRpcClient = client :> IOmpRpcClient
    let nowFn () = DateTimeOffset.UtcNow
    createSessionManager spawn createClient turnEnded nowFn

[<Fact>]
let ``spawn failure makes ensure runtime report error`` () =
    task {
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Error "no omp binary"
        let sm = scriptedSm spawn client (fun _ -> Task.FromResult(()))
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
        let sm = scriptedSm spawn client (fun _ -> Task.FromResult(()))

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
        let sm = scriptedSm spawn client (fun _ -> Task.FromResult(()))

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
        let sm = scriptedSm spawn client (fun _ -> Task.FromResult(()))

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
        let sm = scriptedSm spawn client (fun _ -> Task.FromResult(()))

        let! r = sm.Prompt(UserId 1L, mkCommand 1L 1L "hi")

        match r with
        | Ok() -> ()
        | Error e -> failwith e

        client.PromptCount |> should equal 1
    }

[<Fact>]
let ``agent_start marks the runtime busy`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ -> Task.FromResult(()))
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

        let turnEnded (cmdId: int64) : Task<unit> =
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

        let turnEnded (cmdId: int64) : Task<unit> =
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
        let sm = scriptedSm spawn client (fun _ -> Task.FromResult(()))
        do! sm.Abort(UserId 99L)
        client.AbortCount |> should equal 0
    }

[<Fact>]
let ``handle_event for unknown user is a no-op`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ -> Task.FromResult(()))
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
        let sm = scriptedSm spawn client (fun _ -> Task.FromResult(()))
        sm.IsRuntimeAlive(UserId 99L) |> should be False
    }

[<Fact>]
let ``idle timeout on an exited runtime is a no-op`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ -> Task.FromResult(()))
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
        let sm = scriptedSm spawn client (fun _ -> Task.FromResult(()))

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
        member _.Insert (commandId: int64) (chunkIndex: int) (chatId: ChatId) (randomId: int64) (payload: string) =
            task {
                let e =
                    { Id = int64 entries.Count
                      CommandId = commandId
                      ChunkIndex = chunkIndex
                      ChatId = chatId
                      RandomId = randomId
                      Payload = payload
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

let private mkWorker (inbox: ICommandInbox) (sessions: FakeSessions) : OmpWorker =
    let outbox = FakeOutbox()
    let wake = WakeChannel()
    let heartbeats = ConcurrentDictionary<int64, CancellationTokenSource>()
    // FS0760 requires `new` for IDisposable; fsharplint's redundantNewKeyword
    // rule would flag it, so suppress that rule for this line.
    // fsharplint:disable redundantNewKeyword

    new OmpWorker(inbox, outbox, sessions, wake, heartbeats, NullLogger<OmpWorker>.Instance)

// fsharplint:enable redundantNewKeyword

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
                  Priority = 0 }

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
                  Priority = 0 }

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
                  Priority = 0 }

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
                  Priority = 0 }

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
              Priority = 0 }
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

        let f1 = chunkFrame "seq" 0 2 (int64 first.Length) (Convert.ToBase64String first)
        let f2 = chunkFrame "seq" 1 2 (int64 second.Length) (Convert.ToBase64String second)

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
                    task { return failwith "download exploded" } }

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

    let st, envs = EventFormatter.onEvent ctx EventFormatter.initialState frame
    envs |> should be Empty
    st.Started |> should be False

[<Fact>]
let ``formatter ignores non text_delta assistant event`` () =
    let ctx = { CommandId = 1L; ChatId = ChatId 1L }
    let frame = JsonObject()
    frame["type"] <- "message_update"
    let ev = JsonObject()
    ev["type"] <- "tool_use"
    frame["assistantMessageEvent"] <- (ev :> JsonNode)

    let st, envs = EventFormatter.onEvent ctx EventFormatter.initialState frame
    envs |> should be Empty
    st.Started |> should be False

[<Fact>]
let ``formatter ignores empty delta and keeps state`` () =
    let ctx = { CommandId = 1L; ChatId = ChatId 1L }
    let frame = JsonObject()
    frame["type"] <- "message_update"
    let ev = JsonObject()
    ev["type"] <- "text_delta"
    ev["delta"] <- ""
    frame["assistantMessageEvent"] <- (ev :> JsonNode)

    let st, envs = EventFormatter.onEvent ctx EventFormatter.initialState frame
    envs |> should be Empty
    st.Started |> should be False

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

    let st1, envs1 =
        EventFormatter.onEvent ctx EventFormatter.initialState (mkDelta "hi")

    envs1 |> should haveLength 1
    st1.Started |> should be True

    let st2, envs2 = EventFormatter.onEvent ctx st1 (mkDelta " there")
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
    let st1, _ = EventFormatter.onEvent ctx EventFormatter.initialState delta

    let endFrame = JsonObject()
    endFrame["type"] <- "agent_end"
    // No isTerminal key.

    let st2, envs = EventFormatter.onEvent ctx st1 endFrame
    envs |> should not' (be Empty)
    st2.Started |> should be False

// ---------------------------------------------------------------------------
// SessionManager: get_state non-object + host tool no-match
// ---------------------------------------------------------------------------

[<Fact>]
let ``handle_event host tool call with unknown tool sends nothing`` () =
    task {
        let fakeProc = FakeOmpProcess()
        let client = ScriptedRpcClient()
        let spawn (_: OmpProcessOptions) : Result<IOmpProcess, string> = Ok(fakeProc :> IOmpProcess)
        let sm = scriptedSm spawn client (fun _ -> Task.FromResult(()))
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

                        names |> Seq.length |> should equal 3
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
let ``executor maps flood wait to an error`` () =
    task {
        let transport =
            { new ITelegramTransport with
                member _.Login() =
                    task { return { BotId = 1L; Username = None } }

                member _.SendMessage(_: SendTarget) = task { return Error(FloodWait 5) }
                member _.EditMessage (_: ChatId) (_: int64) (_: string) (_: TelegramEntity list) = Task.FromResult(())
                member _.DownloadVoice(_: VoiceRef) = task { return [||] } }

        let executor =
            HostToolExecutor(transport, FakeVoiceProcessor(Ok "hi"), NullLogger<HostToolExecutor>.Instance)

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_fw"
        frame["toolCallId"] <- "toolu_fw"
        frame["toolName"] <- "tg_send_message"
        let args = JsonObject()
        args["chat_id"] <- 1L
        args["text"] <- "hi"
        frame["arguments"] <- (args :> JsonNode)

        let! result = executor.TryExecute frame

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
                member _.DownloadVoice(_: VoiceRef) = task { return [||] } }

        let executor =
            HostToolExecutor(transport, FakeVoiceProcessor(Ok "hi"), NullLogger<HostToolExecutor>.Instance)

        let frame = JsonObject()
        frame["type"] <- "host_tool_call"
        frame["id"] <- "host_sm"
        frame["toolCallId"] <- "toolu_sm"
        frame["toolName"] <- "tg_send_message"
        let args = JsonObject()
        args["chat_id"] <- 1L
        args["text"] <- "hi"
        frame["arguments"] <- (args :> JsonNode)

        let! result = executor.TryExecute frame

        match result with
        | Some r ->
            Json.getBool "isError" r |> should equal (Some true)
            Assert.Contains("slowmode wait", hostToolResultText r)
        | None -> failwith "expected a host_tool result frame for edit"
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
        let sm = scriptedSm spawn client (fun _ -> Task.FromResult(()))
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
        let sm = scriptedSm spawn client (fun _ -> Task.FromResult(()))

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
        let sm = scriptedSm spawn client (fun _ -> Task.FromResult(()))
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
        let sm = scriptedSm spawn client (fun _ -> Task.FromResult(()))

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
        let sm = scriptedSm spawn client (fun _ -> Task.FromResult(()))
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
        let sm = scriptedSm spawn client (fun _ -> Task.FromResult(()))
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
