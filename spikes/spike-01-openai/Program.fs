// Phase 0 spike: vanbukin OpenAI-compatible endpoint — streaming + tool calls
// through Microsoft.Extensions.AI.OpenAI (10.10.0).
//
// Requires env: VANBUKIN_API_KEY (endpoint https://vanbukin.com/v1, model from models.yml).
// Throwaway: not part of Phos.sln, not run in CI.

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open OpenAI
open System.ClientModel

let apiKey =
    match Environment.GetEnvironmentVariable "VANBUKIN_API_KEY" with
    | null -> failwith "VANBUKIN_API_KEY not set"
    | k -> k

let modelId = "DeepSeek-V4-Flash-Vision-Exp"

let client =
    new OpenAIClient(ApiKeyCredential(apiKey), OpenAIClientOptions(Endpoint = Uri("https://vanbukin.com/v1")))

let chat = client.GetChatClient(modelId).AsIChatClient()

let report (title: string) (body: string) =
    printfn "===== %s =====" title
    printfn "%s" body
    printfn ""

/// Iterate an IAsyncEnumerable, calling `f` for each item.
let iterAsync (source: IAsyncEnumerable<ChatResponseUpdate>) (f: ChatResponseUpdate -> unit) : Task =
    task {
        let e = source.GetAsyncEnumerator(CancellationToken.None)

        try
            let mutable more = true

            while more do
                let! has = e.MoveNextAsync()
                if has then f e.Current else more <- false
        finally
            e.DisposeAsync().AsTask().GetAwaiter().GetResult()
    }

let appendText
    (sb: System.Text.StringBuilder)
    (firstDeltaMs: int64 ref)
    (deltaCount: int ref)
    (sw: Stopwatch)
    (update: ChatResponseUpdate)
    =
    let t = update.Text

    if t.Length > 0 then
        if !firstDeltaMs < 0L then
            firstDeltaMs := sw.ElapsedMilliseconds

        deltaCount.Value <- deltaCount.Value + 1
        sb.Append(t) |> ignore

// ---------------------------------------------------------------------------
// Test 1: streaming chat completion (no tools)
// ---------------------------------------------------------------------------
let runStreaming () =
    task {
        let sw = Stopwatch.StartNew()
        let messages = ResizeArray<ChatMessage>()
        messages.Add(ChatMessage(ChatRole.System, "Отвечай кратко, в одно предложении."))
        messages.Add(ChatMessage(ChatRole.User, "Что такое рекурсия?"))

        let sb = System.Text.StringBuilder()
        let firstDeltaMs = ref -1L
        let deltaCount = ref 0

        let stream = chat.GetStreamingResponseAsync(messages)
        do! iterAsync stream (appendText sb firstDeltaMs deltaCount sw)

        report
            "Test 1: streaming"
            (sprintf
                "time_to_first_delta_ms: %d
delta_count: %d
total_ms: %d
text: %s"
                !firstDeltaMs
                !deltaCount
                sw.ElapsedMilliseconds
                (sb.ToString()))
    }

// ---------------------------------------------------------------------------
// Test 2: tool call round-trip (get_weather), streaming for the final answer
// ---------------------------------------------------------------------------
let runToolCall () =
    task {
        let getWeather (city: string) =
            sprintf "Погода в %s: +23°C, ясно, ветер 3 м/с." city

        let toolOptions =
            AIFunctionFactoryOptions(
                Name = "get_weather",
                Description = "Получить текущую погоду для указанного города."
            )

        let tool = AIFunctionFactory.Create(Func<string, string>(getWeather), toolOptions)
        let tools = ResizeArray<AITool>([| tool :> AITool |])

        let messages = ResizeArray<ChatMessage>()

        messages.Add(
            ChatMessage(
                ChatRole.System,
                "Если пользователь спрашивает о погоде — обязательно вызови инструмент get_weather и ответь на основе его результата."
            )
        )

        messages.Add(ChatMessage(ChatRole.User, "Какая погода в Москве?"))

        let options = ChatOptions(Tools = tools)
        let sw = Stopwatch.StartNew()

        let! response = chat.GetResponseAsync(messages, options)
        let firstRoundMs = sw.ElapsedMilliseconds

        let functionCalls =
            response.Messages
            |> Seq.collect (fun m -> m.Contents)
            |> Seq.choose (fun (c: AIContent) ->
                match c with
                | :? FunctionCallContent as fc -> Some(fc.Name, fc.CallId, fc.Arguments)
                | _ -> None)
            |> Seq.toList

        report
            "Test 2a: tool call"
            (sprintf
                "first_round_ms: %d
finish_reason: %A
text: %s
function_calls: %A"
                firstRoundMs
                response.FinishReason
                response.Text
                functionCalls)

        // Streaming tool-call content observation (does the provider stream tool JSON?)
        let streamCalls = System.Collections.Generic.List<string>()
        let stream = chat.GetStreamingResponseAsync(messages, options)

        do!
            iterAsync stream (fun update ->
                for c in (update.Contents :> seq<AIContent>) do
                    match c with
                    | :? FunctionCallContent as fc -> streamCalls.Add(sprintf "%s#%s" fc.Name fc.CallId)
                    | _ -> ())

        report
            "Test 2b: streaming tool-call deltas"
            (sprintf
                "function_call_content_updates: %d
samples: %A"
                streamCalls.Count
                (streamCalls |> Seq.truncate 3 |> Seq.toList))

        if not (List.isEmpty functionCalls) then
            // Feed the tool result back and stream the final answer.
            let assistantMsg = response.Messages |> Seq.last
            messages.Add(assistantMsg)

            for (_, callId, args) in functionCalls do
                let! result = tool.InvokeAsync(AIFunctionArguments(args))

                report
                    "Test 2c: tool result"
                    (sprintf
                        "call_id: %s
result: %s"
                        callId
                        (string result))

                let toolMsg =
                    ChatMessage(
                        ChatRole.Tool,
                        ResizeArray<AIContent>([| FunctionResultContent(callId, result) :> AIContent |])
                    )

                messages.Add(toolMsg)

            let sb = System.Text.StringBuilder()
            let firstDeltaMs = ref -1L
            let deltaCount = ref 0
            let sw2 = Stopwatch.StartNew()
            let stream2 = chat.GetStreamingResponseAsync(messages, options)
            do! iterAsync stream2 (appendText sb firstDeltaMs deltaCount sw2)

            report
                "Test 2d: final streaming answer"
                (sprintf
                    "time_to_first_delta_ms: %d
delta_count: %d
total_ms: %d
text: %s"
                    !firstDeltaMs
                    !deltaCount
                    sw2.ElapsedMilliseconds
                    (sb.ToString()))
    }

[<EntryPoint>]
let main _ =
    runStreaming().GetAwaiter().GetResult()
    runToolCall().GetAwaiter().GetResult()
    0
