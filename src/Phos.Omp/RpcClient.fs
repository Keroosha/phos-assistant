namespace Phos.Omp

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open System.Text.Json.Nodes
open Microsoft.Extensions.Logging

/// The RPC client boundary used by `SessionManager`. It owns the child proc
/// pipes (stdin/stdout), correlates command responses by `id`, negotiates
/// protocol v2 when advertised, and forwards every non-response frame to
/// `EventReceived`. It knows nothing about session semantics.
type IOmpRpcClient =
    /// Fired for every non-response, non-chunk frame (events, host tool calls,
    /// host URI requests, notices, reassembled v2 objects).
    abstract EventReceived: IEvent<JsonObject>
    /// Fired for malformed frames, oversized physical frames and reassembly
    /// failures. Never thrown.
    abstract ParseError: IEvent<string>
    /// True once the child proc has exited or its stdout closed.
    abstract IsDead: bool
    /// Sends a command frame and awaits the correlated response.
    abstract SendAsync: command: string * payload: JsonNode * ?id: string -> Task<Result<JsonNode, RpcError>>
    /// Sends a raw frame (e.g. `host_tool_result`, `host_uri_result`) with no
    /// response correlation.
    abstract SendRawAsync: frame: JsonObject -> Task<unit>
    abstract PromptAsync: message: string * ?streamingBehavior: string -> Task<Result<JsonNode, RpcError>>
    abstract AbortAsync: unit -> Task<Result<JsonNode, RpcError>>
    abstract AbortAndPromptAsync: message: string -> Task<Result<JsonNode, RpcError>>
    abstract FollowUpAsync: message: string -> Task<Result<JsonNode, RpcError>>
    abstract GetStateAsync: unit -> Task<Result<JsonNode, RpcError>>
    abstract GetLastAssistantTextAsync: unit -> Task<Result<JsonNode, RpcError>>
    abstract SetHostToolsAsync: tools: RpcHostToolDefinition list -> Task<Result<JsonNode, RpcError>>
    abstract SetHostUriSchemesAsync: schemes: RpcHostUriScheme list -> Task<Result<JsonNode, RpcError>>
    abstract GetAvailableCommandsAsync: unit -> Task<Result<JsonNode, RpcError>>
    abstract Dispose: unit -> unit

/// Concrete JSONL RPC client over a child proc's stdio.
type OmpRpcClient(proc: Process, logger: ILogger, ?readyFrame: JsonObject) =
    let stdin = proc.StandardInput
    let stdout = proc.StandardOutput
    let eventReceived = Event<JsonObject>()
    let parseError = Event<string>()

    let pending =
        ConcurrentDictionary<string, TaskCompletionSource<Result<JsonNode, RpcError>>>()

    let cts = new CancellationTokenSource()

    let mutable reassembler = RpcChunkReassembler(67_108_864L)
    let mutable maxFrameBytes = 1_048_576L
    let mutable nextId = 0
    let mutable dead = false

    let writeFrame (frame: JsonObject) : bool =
        try
            stdin.WriteLine(frame.ToJsonString())
            stdin.Flush()
            true
        with ex ->
            logger.LogError(ex, "omp rpc write failed")
            dead <- true
            false

    let supportsV2 (versions: JsonArray) : bool =
        versions
        |> Seq.exists (fun n ->
            match n with
            | :? JsonValue as v ->
                try
                    v.GetValue<int>() = 2
                with _ ->
                    try
                        v.GetValue<int64>() = 2L
                    with _ ->
                        false
            | _ -> false)

    let handleReady (frame: JsonObject) : unit =
        Json.getInt64 "maxFrameBytes" frame |> Option.iter (fun m -> maxFrameBytes <- m)

        Json.getInt64 "maxReassembledFrameBytes" frame
        |> Option.iter (fun m -> reassembler <- RpcChunkReassembler(m))

        match Json.getArray "supportedProtocolVersions" frame with
        | Some versions when supportsV2 versions ->
            let neg = JsonObject()
            neg["id"] <- "protocol-1"
            neg["type"] <- "negotiate_protocol"
            neg["protocolVersion"] <- 2
            writeFrame neg |> ignore
        | _ -> ()

    let handleResponse (frame: JsonObject) : unit =
        match Json.getString "id" frame with
        | Some id ->
            match pending.TryRemove id with
            | true, tcs ->
                match Json.getBool "success" frame with
                | Some true ->
                    let data = defaultArg (Json.tryGet "data" frame) (JsonObject())
                    tcs.TrySetResult(Ok data) |> ignore
                | _ ->
                    let err =
                        { Command = defaultArg (Json.getString "command" frame) ""
                          Code = Json.getString "code" frame
                          Message = defaultArg (Json.getString "error" frame) "" }

                    tcs.TrySetResult(Error err) |> ignore
            | false, _ -> ()
        | None -> parseError.Trigger "response frame without id"

    let dispatchFrame (frame: JsonObject) : unit =
        match RpcProtocol.classify frame with
        | FrameKind.Ready -> handleReady frame
        | FrameKind.Response -> handleResponse frame
        | _ -> eventReceived.Trigger frame

    let handleLine (line: string) : unit =
        if String.IsNullOrWhiteSpace line then
            ()
        elif int64 line.Length > maxFrameBytes then
            parseError.Trigger(sprintf "physical frame exceeds maxFrameBytes: %d" line.Length)
        else
            match RpcProtocol.tryParseFrame line with
            | Error e -> parseError.Trigger e
            | Ok frame ->
                match RpcProtocol.classify frame with
                | FrameKind.RpcChunk ->
                    match reassembler.TryAdd frame with
                    | Ok None -> ()
                    | Ok(Some obj) -> dispatchFrame obj
                    | Error e -> parseError.Trigger e
                | _ -> dispatchFrame frame

    let readerTask: Task =
        task {
            let mutable running = true

            while running && not cts.IsCancellationRequested do
                let! line = stdout.ReadLineAsync()

                match line with
                | null ->
                    running <- false
                    dead <- true

                    for kv in pending do
                        kv.Value.TrySetResult(
                            Error
                                { Command = "closed"
                                  Code = Some "eof"
                                  Message = "omp proc stdout closed" }
                        )
                        |> ignore

                    pending.Clear()
                | line -> handleLine line
        }

    // Core send: builds a command frame, registers a pending TCS, writes it and
    // awaits the correlated response.
    let send (command: string) (payload: JsonNode) (id: string option) : Task<Result<JsonNode, RpcError>> =
        let id = defaultArg id (sprintf "req_%d" (Interlocked.Increment(&nextId)))
        let frame = JsonObject()
        frame["id"] <- id
        frame["type"] <- command

        match payload with
        | :? JsonObject as obj ->
            for kv in obj do
                match kv.Value with
                | null -> ()
                | v -> frame.[kv.Key] <- v.DeepClone()
        | _ -> frame["data"] <- payload.DeepClone()

        if dead then
            Task.FromResult(
                Error
                    { Command = command
                      Code = Some "closed"
                      Message = "RPC client is closed" }
            )
        else
            let tcs =
                TaskCompletionSource<Result<JsonNode, RpcError>>(TaskCreationOptions.RunContinuationsAsynchronously)

            pending.[id] <- tcs

            // If the write fails (e.g. the child proc's stdin pipe is closed),
            // resolve the pending TCS with an Error so `SendAsync` never hangs
            // forever awaiting a response that cannot arrive.
            if writeFrame frame then
                tcs.Task
            else
                pending.TryRemove id |> ignore

                tcs.TrySetResult(
                    Error
                        { Command = command
                          Code = Some "write"
                          Message = "failed to write RPC frame" }
                )
                |> ignore

                tcs.Task

    do
        readyFrame |> Option.iter handleReady
        readerTask |> ignore

    interface IOmpRpcClient with
        member _.EventReceived = eventReceived.Publish
        member _.ParseError = parseError.Publish
        member _.IsDead = dead

        member _.SendAsync(command: string, payload: JsonNode, ?id: string) : Task<Result<JsonNode, RpcError>> =
            send command payload id

        member _.SendRawAsync(frame: JsonObject) : Task<unit> = task { writeFrame frame |> ignore }

        member _.PromptAsync(message: string, ?streamingBehavior: string) : Task<Result<JsonNode, RpcError>> =
            let payload = JsonObject()
            payload["message"] <- message
            streamingBehavior |> Option.iter (fun sb -> payload["streamingBehavior"] <- sb)
            send "prompt" payload None

        member _.AbortAsync() : Task<Result<JsonNode, RpcError>> = send "abort" (JsonObject()) None

        member _.AbortAndPromptAsync(message: string) : Task<Result<JsonNode, RpcError>> =
            let payload = JsonObject()
            payload["message"] <- message
            send "abort_and_prompt" payload None

        member _.FollowUpAsync(message: string) : Task<Result<JsonNode, RpcError>> =
            let payload = JsonObject()
            payload["message"] <- message
            send "follow_up" payload None

        member _.GetStateAsync() : Task<Result<JsonNode, RpcError>> = send "get_state" (JsonObject()) None

        member _.GetLastAssistantTextAsync() : Task<Result<JsonNode, RpcError>> =
            send "get_last_assistant_text" (JsonObject()) None

        member _.SetHostToolsAsync(tools: RpcHostToolDefinition list) : Task<Result<JsonNode, RpcError>> =
            let payload = JsonObject()
            let arr = JsonArray()

            for t in tools do
                arr.Add(RpcProtocol.hostToolDefinitionToJson t)

            payload["tools"] <- (arr :> JsonNode)
            send "set_host_tools" payload None

        member _.SetHostUriSchemesAsync(schemes: RpcHostUriScheme list) : Task<Result<JsonNode, RpcError>> =
            let payload = JsonObject()
            let arr = JsonArray()

            for s in schemes do
                arr.Add(RpcProtocol.hostUriSchemeToJson s)

            payload["schemes"] <- (arr :> JsonNode)
            send "set_host_uri_schemes" payload None

        member _.GetAvailableCommandsAsync() : Task<Result<JsonNode, RpcError>> =
            send "get_available_commands" (JsonObject()) None

        member _.Dispose() =
            cts.Cancel()

            try
                stdin.Close()
            with _ ->
                ()

            try
                if not proc.HasExited then
                    proc.WaitForExit(5000) |> ignore
            with _ ->
                ()

            try
                if not proc.HasExited then
                    proc.Kill(true)
            with _ ->
                ()

            try
                proc.Dispose()
            with _ ->
                ()
