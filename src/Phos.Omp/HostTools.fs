namespace Phos.Omp

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open System.Text.Json.Nodes
open Microsoft.Extensions.Logging
open Phos.Core
open Phos.Core.DomainTypes
open Phos.Core.ScheduleJobs
open Phos.Core.SchedulePolicy
open Phos.Storage
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
            Parameters = jsonSchema [ "chat_id", "integer", true; "message_id", "integer", true ] }
          { Name = "schedule_add"
            Label = "Add schedule job"
            Description =
              "Create a scheduled prompt. Provide exactly one of run_at, cron_expr, interval_seconds or after_seconds. Use run_at for a one-time calendar date; cron repeats."
            Parameters =
              jsonSchema
                  [ "prompt", "string", true
                    "run_at", "string", false
                    "cron_expr", "string", false
                    "interval_seconds", "integer", false
                    "after_seconds", "integer", false
                    "timezone", "string", false
                    "catch_up", "string", false
                    "chat_id", "integer", false ] }
          { Name = "schedule_confirm"
            Label = "Confirm schedule job"
            Description = "Confirm a pending schedule job so it becomes active."
            Parameters = jsonSchema [ "job_id", "integer", true ] }
          { Name = "schedule_cancel"
            Label = "Cancel schedule job"
            Description = "Cancel a pending schedule job."
            Parameters = jsonSchema [ "job_id", "integer", true ] }
          { Name = "schedule_pause"
            Label = "Pause schedule job"
            Description = "Pause an active schedule job."
            Parameters = jsonSchema [ "job_id", "integer", true ] }
          { Name = "schedule_resume"
            Label = "Resume schedule job"
            Description = "Resume a paused schedule job."
            Parameters = jsonSchema [ "job_id", "integer", true ] }
          { Name = "schedule_remove"
            Label = "Remove schedule job"
            Description = "Remove a schedule job."
            Parameters = jsonSchema [ "job_id", "integer", true ] }
          { Name = "schedule_run_now"
            Label = "Run schedule job now"
            Description = "Trigger a schedule job on the next tick."
            Parameters = jsonSchema [ "job_id", "integer", true ] }
          { Name = "schedule_list"
            Label = "List schedule jobs"
            Description = "List the user's schedule jobs."
            Parameters = jsonSchema [] } ]

/// Executes `host_tool_call` frames for the registered Telegram tools and
/// produces the matching `host_tool_result` frames. Execution is idempotent per
/// `toolCallId` (best-effort, in-memory): a repeated call for the same id
/// returns the cached result without repeating the side effect.
type HostToolExecutor
    (
        transport: ITelegramTransport,
        voice: IVoiceProcessor,
        jobs: IScheduleJobRepository,
        quota: ScheduleQuota,
        logger: ILogger
    ) =

    // toolCallId -> (text, isError); in-memory best-effort idempotency cache.
    let cache = ConcurrentDictionary<string, string * bool>()

    let sendErrorToMessage (err: SendError) : string =
        match err with
        | FloodWait s -> sprintf "telegram flood wait %ds" s
        | SlowModeWait s -> sprintf "telegram slowmode wait %ds" s
        | MissingPeer _ -> "peer not resolved yet, try again later"
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

    let scheduleAdd
        (userId: UserId)
        (chatId: ChatId option)
        (toolCallId: string)
        (args: JsonObject)
        : Task<Result<string, string>> =
        task {
            match! jobs.FindByToolCallId toolCallId with
            | Some job -> return Ok(sprintf "задание #%d уже создано (pending)" job.Id)
            | None ->
                match Json.getString "prompt" args with
                | None -> return Error "поле prompt обязательно"
                | Some prompt ->
                    let cron = Json.getString "cron_expr" args
                    let interval = Json.getInt "interval_seconds" args
                    let after = Json.getInt64 "after_seconds" args |> Option.map int
                    let timezone = Json.getString "timezone" args |> Option.defaultValue TimeZoneInfo.Local.Id
                    let now = DateTimeOffset.UtcNow

                    let runAt =
                        match Json.getString "run_at" args with
                        | None -> Ok None
                        | Some value ->
                            match ScheduleJobs.parseRunAt timezone value with
                            | Error e -> Error e
                            | Ok resolved -> Ok(Some resolved)

                    match runAt with
                    | Error e -> return Error e
                    | Ok runAt ->
                        let catchup =
                            match Json.getString "catch_up" args with
                            | Some "once" -> CatchUpOnce
                            | _ -> SkipMissed

                        let targetChatId =
                            match Json.getInt64 "chat_id" args, chatId with
                            | Some cid, _ -> Some(ChatId cid)
                            | None, Some cid -> Some cid
                            | None, None -> None

                        match targetChatId with
                        | None -> return Error "неизвестен chat_id"
                        | Some targetChatId ->
                            let! active = jobs.CountActiveForUser userId

                            if active >= quota.MaxJobsPerUser then
                                return Error(sprintf "достигнут лимит заданий (%d)" quota.MaxJobsPerUser)
                            else
                                let draft =
                                    { UserId = userId
                                      ChatId = targetChatId
                                      Prompt = prompt
                                      CronExpr = cron
                                      IntervalSeconds = interval
                                      AfterSeconds = after
                                      RunAt = runAt
                                      Timezone = timezone
                                      Catchup = catchup }

                                match ScheduleJobs.validateAt quota now draft with
                                | Error e -> return Error e
                                | Ok validDraft ->
                                    let! job = jobs.Insert validDraft (Some toolCallId)

                                    if validDraft.RunAt.IsSome || validDraft.AfterSeconds.IsSome then
                                        let timeText =
                                            ScheduleJobs.nextRunAfter validDraft now
                                            |> Option.map (fun occ -> ScheduleJobs.formatOccurrences validDraft.Timezone [ occ ])
                                            |> Option.defaultValue ""

                                        let text =
                                            sprintf
                                                "Задание #%d создано (сработает один раз, ожидает подтверждения).\nСработает примерно в %s\nСпроси у пользователя подтверждение."
                                                job.Id
                                                timeText

                                        return Ok text
                                    else
                                        let next5 =
                                            ScheduleJobs.nextOccurrences
                                                job.CronExpr
                                                job.IntervalSeconds
                                                job.Timezone
                                                now
                                                5

                                        let text =
                                            sprintf
                                                "Задание #%d создано (ожидает подтверждения). Ближайшие:\n%s\nСпроси у пользователя подтверждение."
                                                job.Id
                                                (ScheduleJobs.formatOccurrences job.Timezone next5)

                                        return Ok text
        }

    let scheduleConfirm (origin: Origin option) (args: JsonObject) : Task<Result<string, string>> =
        task {
            match Json.getInt64 "job_id" args with
            | None -> return Error "поле job_id обязательно"
            | Some id ->
                match origin with
                | Some Telegram ->
                    do! jobs.Confirm id
                    return Ok(sprintf "Задание #%d активно." id)
                | _ -> return Error "подтверждение доступно только из хода пользователя"
        }

    let scheduleCancel (args: JsonObject) : Task<Result<string, string>> =
        task {
            match Json.getInt64 "job_id" args with
            | None -> return Error "поле job_id обязательно"
            | Some id ->
                do! jobs.Cancel id
                return Ok(sprintf "Задание #%d отменено." id)
        }

    let scheduleList (userId: UserId) (args: JsonObject) : Task<Result<string, string>> =
        task {
            let! userJobs = jobs.ListForUser userId

            let lines =
                userJobs
                |> List.map (fun job ->
                    let prompt =
                        if job.Prompt.Length > 80 then
                            job.Prompt.Substring(0, 80)
                        else
                            job.Prompt

                    let next =
                        job.NextRun
                        |> Option.map (fun dto -> dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm"))
                        |> Option.defaultValue ""

                    let statusText =
                        let s = ScheduleJobs.statusToString job.Status

                        if job.AfterSeconds.IsSome || job.RunAt.IsSome then s + " (разово)" else s

                    sprintf "#%d [%s] %s next: %s" job.Id statusText prompt next)
            return Ok(String.concat "\n" lines)
        }

    let schedulePause (args: JsonObject) : Task<Result<string, string>> =
        task {
            match Json.getInt64 "job_id" args with
            | None -> return Error "поле job_id обязательно"
            | Some id ->
                do! jobs.Pause id
                return Ok(sprintf "Задание #%d приостановлено." id)
        }

    let scheduleResume (args: JsonObject) : Task<Result<string, string>> =
        task {
            match Json.getInt64 "job_id" args with
            | None -> return Error "поле job_id обязательно"
            | Some id ->
                do! jobs.Resume id
                return Ok(sprintf "Задание #%d возобновлено." id)
        }

    let scheduleRemove (args: JsonObject) : Task<Result<string, string>> =
        task {
            match Json.getInt64 "job_id" args with
            | None -> return Error "поле job_id обязательно"
            | Some id ->
                do! jobs.Remove id
                return Ok(sprintf "Задание #%d удалено." id)
        }

    let scheduleRunNow (args: JsonObject) : Task<Result<string, string>> =
        task {
            match Json.getInt64 "job_id" args with
            | None -> return Error "поле job_id обязательно"
            | Some id ->
                do! jobs.RunNow id
                return Ok(sprintf "Задание #%d запущено (сработает на ближайшем тике)." id)
        }

    let execute
        (userId: UserId)
        (chatId: ChatId option)
        (origin: Origin option)
        (toolCallId: string)
        (toolName: string)
        (args: JsonObject)
        : Task<Result<string, string>> =
        match toolName with
        | "tg_send_message" -> sendMessage args
        | "tg_edit_message" -> editMessage args
        | "stt_transcribe" -> transcribe args
        | "schedule_add" -> scheduleAdd userId chatId toolCallId args
        | "schedule_confirm" -> scheduleConfirm origin args
        | "schedule_cancel" -> scheduleCancel args
        | "schedule_list" -> scheduleList userId args
        | "schedule_pause" -> schedulePause args
        | "schedule_resume" -> scheduleResume args
        | "schedule_remove" -> scheduleRemove args
        | "schedule_run_now" -> scheduleRunNow args
        | _ -> Task.FromResult(Error(sprintf "unknown host tool: %s" toolName))

    /// Executes a frame if it is a `host_tool_call`, returning the
    /// `host_tool_result` frame to send, or `None` for any other frame.
    member _.TryExecute
        (userId: UserId, chatId: ChatId option, origin: Origin option, frame: JsonObject)
        : Task<JsonObject option> =
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
                    try
                        let! outcome = execute userId chatId origin toolCallId toolName args

                        match outcome with
                        | Ok text ->
                            cache[toolCallId] <- (text, false)
                            return Some(buildResult id text false)
                        | Error msg ->
                            cache[toolCallId] <- (msg, true)
                            logger.LogWarning("host tool {Tool} failed: {Error}", toolName, msg)
                            return Some(buildResult id msg true)
                    with ex ->
                        logger.LogError(ex, "host tool {Tool} threw", toolName)
                        let msg = sprintf "внутренняя ошибка: %s" ex.Message
                        cache[toolCallId] <- (msg, true)
                        return Some(buildResult id msg true)
        }
