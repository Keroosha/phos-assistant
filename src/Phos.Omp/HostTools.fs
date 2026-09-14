namespace Phos.Omp

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open System.Text.Json.Nodes
open Microsoft.Extensions.Logging
open Phos.Core.DomainTypes
open Phos.Telegram

/// Host-owned Telegram tools exposed to the agent via `set_host_tools`.
module HostTools =

    let private jsonSchema (props: (string * string * bool) list) : JsonObject =
        let obj = JsonObject()
        obj["type"] <- "object"
        let propsObj = JsonObject()
        let required = JsonArray()

        for name, ptype, isRequired in props do
            let p = JsonObject()
            p["type"] <- ptype
            propsObj[name] <- p

            if isRequired then
                required.Add(name)

        obj["properties"] <- propsObj
        obj["required"] <- required
        obj

    /// The host tool definitions registered on every session.
    let definitions: RpcHostToolDefinition list =
        [ { Name = "tg_send_message"
            Label = "Send Telegram message"
            Description = "Send a text message to a Telegram chat."
            Parameters = jsonSchema [ "chat_id", "integer", true; "text", "string", true ] }
          { Name = "tg_edit_message"
            Label = "Edit Telegram message"
            Description = "Edit an existing Telegram message."
            Parameters =
              jsonSchema
                  [ "chat_id", "integer", true
                    "message_id", "integer", true
                    "text", "string", true ] }
          { Name = "stt_transcribe"
            Label = "Transcribe voice"
            Description = "Transcribe a Telegram voice message."
            Parameters = jsonSchema [ "chat_id", "integer", true; "message_id", "integer", true ] } ]

/// Executes `host_tool_call` frames for the registered Telegram tools and
/// produces the matching `host_tool_result` frames. Execution is idempotent per
/// `toolCallId` (best-effort, in-memory): a repeated call for the same id
/// returns the cached result without repeating the side effect.
type HostToolExecutor(transport: ITelegramTransport, voice: IVoiceProcessor, logger: ILogger) =

    // toolCallId -> (text, isError); in-memory best-effort idempotency cache.
    let cache = ConcurrentDictionary<string, string * bool>()

    let sendErrorToMessage (err: SendError) : string =
        match err with
        | FloodWait s -> sprintf "telegram flood wait %ds" s
        | SlowModeWait s -> sprintf "telegram slowmode wait %ds" s
        | Other msg -> msg

    let buildResult (id: string) (text: string) (isError: bool) : JsonObject =
        let result = JsonObject()
        result["type"] <- "host_tool_result"
        result["id"] <- id

        if isError then
            result["isError"] <- true

        let content = JsonArray()
        let item = JsonObject()
        item["type"] <- "text"
        item["text"] <- text
        content.Add(item)
        let resObj = JsonObject()
        resObj["content"] <- content
        result["result"] <- resObj
        result

    let sendMessage (args: JsonObject) : Task<Result<string, string>> =
        task {
            match Json.getInt64 "chat_id" args, Json.getString "text" args with
            | Some chatId, Some text ->
                let target =
                    { ChatId = ChatId chatId
                      Text = text
                      Entities = []
                      RandomId = Random.Shared.NextInt64() }

                let! result = transport.SendMessage target

                match result with
                | Ok _ -> return Ok "sent"
                | Error err -> return Error(sendErrorToMessage err)
            | _ -> return Error "tg_send_message requires chat_id and text"
        }

    let editMessage (args: JsonObject) : Task<Result<string, string>> =
        task {
            match Json.getInt64 "chat_id" args, Json.getInt64 "message_id" args, Json.getString "text" args with
            | Some chatId, Some messageId, Some text ->
                do! transport.EditMessage (ChatId chatId) messageId text []
                return Ok "edited"
            | _ -> return Error "tg_edit_message requires chat_id, message_id and text"
        }

    let transcribe (args: JsonObject) : Task<Result<string, string>> =
        task {
            match Json.getInt64 "chat_id" args, Json.getInt64 "message_id" args with
            | Some chatId, Some messageId ->
                let voiceRef =
                    { ChatId = ChatId chatId
                      MessageId = messageId
                      FileReference = [||]
                      AccessHash = 0L }

                let! result = voice.ProcessAsync voiceRef
                return result
            | _ -> return Error "stt_transcribe requires chat_id and message_id"
        }

    let execute (toolName: string) (args: JsonObject) : Task<Result<string, string>> =
        match toolName with
        | "tg_send_message" -> sendMessage args
        | "tg_edit_message" -> editMessage args
        | "stt_transcribe" -> transcribe args
        | _ -> Task.FromResult(Error(sprintf "unknown host tool: %s" toolName))

    /// Executes a frame if it is a `host_tool_call`, returning the
    /// `host_tool_result` frame to send, or `None` for any other frame.
    member _.TryExecute(frame: JsonObject) : Task<JsonObject option> =
        task {
            if RpcProtocol.classify frame <> FrameKind.HostToolCall then
                return None
            else
                let id = Json.getString "id" frame |> Option.defaultValue ""
                let toolCallId = Json.getString "toolCallId" frame |> Option.defaultValue ""
                let toolName = Json.getString "toolName" frame |> Option.defaultValue ""
                let args = Json.getObject "arguments" frame |> Option.defaultValue (JsonObject())

                match cache.TryGetValue toolCallId with
                | true, (text, isError) ->
                    // Duplicate call: replay cached result without repeating the side effect.
                    return Some(buildResult id text isError)
                | _ ->
                    let! outcome = execute toolName args

                    match outcome with
                    | Ok text ->
                        cache[toolCallId] <- (text, false)
                        return Some(buildResult id text false)
                    | Error msg ->
                        cache[toolCallId] <- (msg, true)
                        logger.LogWarning("host tool {Tool} failed: {Error}", toolName, msg)
                        return Some(buildResult id msg true)
        }
