namespace Phos.IntegrationTests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Phos.Omp

/// The response behaviour a fake LLM endpoint should exhibit for a single
/// chat-completions request. Deterministic and scriptable per test.
type FakeLlmBehavior =
    /// Emit a fixed assistant text as a short stream.
    | TextOnly of text: string
    /// Emit the text word-by-word with a per-word delay (used to keep a turn
    /// long enough to abort mid-stream).
    | SlowStream of text: string * delayMs: int
    /// First request emits a tool call; once the request messages contain a
    /// `role: "tool"` result, emit the final text.
    | ToolCall of name: string * argsJson: string * finalText: string

/// An OpenAI-compatible `/v1/chat/completions` + `/v1/models` fake server on a
/// loopback-only random port. Uses `System.Net.HttpListener`. Every test gets its
/// own instance; `Dispose` stops the listener.
type FakeLlmServer(initialBehavior: FakeLlmBehavior) =
    let mutable behavior = initialBehavior
    let mutable requestCount = 0
    let mutable lastMessages: JsonArray option = None
    let mutable port = 0
    let requests = ResizeArray<string list>()
    let lastUserMessages = ResizeArray<string>()
    let listener = new HttpListener()
    let cts = new CancellationTokenSource()

    let jsonChunk (delta: JsonObject) (finishReason: string option) : JsonObject =
        let obj = JsonObject()
        obj["id"] <- "x"
        obj["object"] <- "chat.completion.chunk"
        let choices = JsonArray()
        let choice = JsonObject()
        choice["index"] <- 0
        choice["delta"] <- delta

        match finishReason with
        | Some fr -> choice["finish_reason"] <- fr
        | None -> choice["finish_reason"] <- null

        choices.Add(choice)
        obj["choices"] <- choices
        obj

    let writeBytes (ctx: HttpListenerContext) (s: string) : unit =
        let bytes = Encoding.UTF8.GetBytes s
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
        ctx.Response.OutputStream.Flush()

    let writeData (ctx: HttpListenerContext) (obj: JsonObject) : unit =
        writeBytes ctx ("data: " + obj.ToJsonString() + "\n\n")

    let roleDelta: JsonObject =
        let d = JsonObject()
        d["role"] <- "assistant"
        d["content"] <- ""
        d

    let contentDelta (content: string) : JsonObject =
        let d = JsonObject()
        d["content"] <- content
        d

    let toolCallDelta (name: string) (argsJson: string) : JsonObject =
        let d = JsonObject()
        d["role"] <- "assistant"
        d["content"] <- null
        let calls = JsonArray()
        let call = JsonObject()
        call["index"] <- 0
        call["id"] <- "call_1"
        call["type"] <- "function"
        let fn = JsonObject()
        fn["name"] <- name
        fn["arguments"] <- ""
        call["function"] <- fn
        calls.Add(call)
        d["tool_calls"] <- calls
        d

    let toolArgsDelta (argsJson: string) : JsonObject =
        let d = JsonObject()
        let calls = JsonArray()
        let call = JsonObject()
        call["index"] <- 0
        let fn = JsonObject()
        fn["arguments"] <- argsJson
        call["function"] <- fn
        calls.Add(call)
        d["tool_calls"] <- calls
        d

    /// Streams `text` as word deltas, pausing `delayMs` between words.
    let streamText (ctx: HttpListenerContext) (text: string) (delayMs: int) : Task =
        task {
            writeData ctx (jsonChunk roleDelta None)

            let words =
                text.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun w -> w + " ")

            let last = words.Length - 1

            for i = 0 to last do
                let content = if i = last then words.[i].TrimEnd() else words.[i]
                writeData ctx (jsonChunk (contentDelta content) None)

                if delayMs > 0 then
                    do! Task.Delay(delayMs)

            writeData ctx (jsonChunk (JsonObject()) (Some "stop"))
            writeBytes ctx "data: [DONE]\n\n"
            ctx.Response.Close()
        }

    /// Streams a tool call for `name`/`argsJson`, then the finish.
    let streamToolCall (ctx: HttpListenerContext) (name: string) (argsJson: string) : Task =
        task {
            writeData ctx (jsonChunk (toolCallDelta name argsJson) None)
            writeData ctx (jsonChunk (toolArgsDelta argsJson) None)
            writeData ctx (jsonChunk (JsonObject()) (Some "tool_calls"))
            writeBytes ctx "data: [DONE]\n\n"
            ctx.Response.Close()
        }

    let contentText (node: JsonNode option) : string option =
        match node with
        | None -> None
        | Some n ->
            match n with
            | :? JsonObject as o ->
                match Json.getString "content" o with
                | Some s -> Some s
                | None ->
                    match Json.getArray "content" o with
                    | Some arr ->
                        arr
                        |> Seq.choose (fun item ->
                            match item with
                            | :? JsonObject as io -> Json.getString "text" io
                            | _ -> None)
                        |> Seq.tryHead
                    | None -> None
            | _ -> None

    let handleChat (ctx: HttpListenerContext) (body: JsonNode) : Task =
        task {
            let messages =
                match body with
                | :? JsonObject as o ->
                    match Json.getArray "messages" o with
                    | Some a -> a
                    | None -> JsonArray()
                | _ -> JsonArray()

            lastMessages <- Some messages
            requestCount <- requestCount + 1

            requests.Add(
                messages
                |> Seq.map (fun m ->
                    match m with
                    | :? JsonObject as o -> Json.getString "role" o |> Option.defaultValue "?"
                    | _ -> "?")
                |> Seq.toList
            )

            lastUserMessages.Add(
                messages
                |> Seq.choose (fun m ->
                    match m with
                    | :? JsonObject as o when Json.getString "role" o = Some "user" -> contentText (Option.ofObj m)
                    | _ -> None)
                |> Seq.tryLast
                |> Option.defaultValue ""
            )

            let hasTool =
                messages
                |> Seq.exists (fun m ->
                    match m with
                    | :? JsonObject as o -> Json.getString "role" o = Some "tool"
                    | _ -> false)

            ctx.Response.StatusCode <- 200
            ctx.Response.ContentType <- "text/event-stream"
            ctx.Response.Headers.Add("Cache-Control", "no-cache")

            match behavior with
            | TextOnly text -> do! streamText ctx text 0
            | SlowStream(text, delayMs) -> do! streamText ctx text delayMs
            | ToolCall(name, argsJson, finalText) ->
                if hasTool then
                    do! streamText ctx finalText 0
                else
                    do! streamToolCall ctx name argsJson
        }

    let handle (ctx: HttpListenerContext) : Task =
        task {
            try
                let req = ctx.Request
                let path = req.RawUrl

                if req.HttpMethod = "GET" && path = "/v1/models" then
                    let obj = JsonObject()
                    obj["object"] <- "list"
                    let data = JsonArray()
                    let model = JsonObject()
                    model["id"] <- "fake-model"
                    model["object"] <- "model"
                    data.Add(model)
                    obj["data"] <- data
                    let bytes = Encoding.UTF8.GetBytes(obj.ToJsonString())
                    ctx.Response.StatusCode <- 200
                    ctx.Response.ContentType <- "application/json"
                    ctx.Response.ContentLength64 <- int64 bytes.Length
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
                    ctx.Response.Close()
                elif req.HttpMethod = "POST" && path = "/v1/chat/completions" then
                    use reader = new StreamReader(req.InputStream, Encoding.UTF8)
                    let! bodyText = reader.ReadToEndAsync()

                    let body =
                        JsonNode.Parse(bodyText) |> Option.ofObj |> Option.defaultValue (JsonObject())

                    do! handleChat ctx body
                else
                    ctx.Response.StatusCode <- 404
                    ctx.Response.Close()
            with ex ->
                Console.Error.WriteLine("fake-llm error: {0}", ex)

                try
                    ctx.Response.StatusCode <- 500
                    ctx.Response.Close()
                with _ ->
                    ()
        }

    let rec acceptLoop () : Task =
        task {
            while not cts.IsCancellationRequested do
                try
                    let! ctx = listener.GetContextAsync()
                    Task.Run(fun () -> handle ctx) |> ignore
                with
                | :? HttpListenerException -> ()
                | :? ObjectDisposedException -> ()
                | _ -> ()
        }

    /// Starts the listener on a random loopback port.
    member _.Start() : unit =
        let tcp = new TcpListener(IPAddress.Loopback, 0)
        tcp.Start()
        port <- (tcp.LocalEndpoint :?> IPEndPoint).Port
        tcp.Stop()
        listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
        listener.Start()
        Task.Run(fun () -> acceptLoop ()) |> ignore

    member _.Behavior
        with get () = behavior
        and set (b: FakeLlmBehavior) = behavior <- b

    /// Number of `/v1/chat/completions` requests served.
    member _.RequestCount = requestCount

    /// Message roles of each request (diagnostics).
    member _.Requests = requests

    /// Last `user` message text of each request (diagnostics).
    member _.LastUserMessages = lastUserMessages

    /// Messages of the most recent request.
    member _.LastMessages = lastMessages

    /// The bound loopback port.
    member _.Port = port

    /// `http://127.0.0.1:<port>/v1` base URL for `models.yml`.
    member _.BaseUrl = sprintf "http://127.0.0.1:%d/v1" port

    member _.Stop() : unit =
        try
            cts.Cancel()
        with _ ->
            ()

        try
            listener.Stop()
        with _ ->
            ()

        try
            listener.Close()
        with _ ->
            ()

    interface IDisposable with
        member this.Dispose() = this.Stop()
