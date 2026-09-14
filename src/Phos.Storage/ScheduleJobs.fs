namespace Phos.Storage

open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Phos.Core
open Phos.Core.DomainTypes
open Phos.Core.ScheduleJobs
open Phos.Core.SchedulePolicy

/// A claim for a due schedule occurrence: the job, the occurrence being fired,
/// and the id of the command enqueued for it.
type ScheduleRunClaim =
    { Job: ScheduleJob
      ScheduledFor: DateTimeOffset
      CommandId: int64 }

/// Durable repository for schedule jobs: CRUD, status transitions and the
/// atomic due-claim that enqueues exactly one command per occurrence.
type IScheduleJobRepository =
    abstract Insert: draft: ScheduleJobDraft -> originToolCallId: string option -> Task<ScheduleJob>
    abstract FindByToolCallId: string -> Task<ScheduleJob option>
    abstract GetById: int64 -> Task<ScheduleJob option>
    abstract ListForUser: UserId -> Task<ScheduleJob list>
    abstract CountActiveForUser: UserId -> Task<int>
    abstract Confirm: int64 -> Task<unit>
    abstract Cancel: int64 -> Task<unit>
    abstract Pause: int64 -> Task<unit>
    abstract Resume: int64 -> Task<unit>
    abstract Remove: int64 -> Task<unit>
    abstract RunNow: int64 -> Task<unit>
    abstract ExpirePending: now: DateTimeOffset -> olderThan: DateTimeOffset -> Task<int>
    abstract PauseDueToErrors: int64 -> string -> Task<unit>
    abstract ClaimDueOccurrence: now: DateTimeOffset -> Task<ScheduleRunClaim option>

type ScheduleJobRepository(exec: StorageExecutor) =
    let userId (UserId id) = id
    let chatId (ChatId id) = id

    let toUnix (dto: DateTimeOffset) = dto.ToUnixTimeSeconds()
    let fromUnix (s: int64) = DateTimeOffset.FromUnixTimeSeconds s

    let addOpt (cmd: SqliteCommand) (name: string) (v: 'T option) =
        cmd.Parameters.AddWithValue(
            name,
            match v with
            | Some x -> box x
            | None -> box DBNull.Value
        )
        |> ignore

    let catchupOfString (s: string) : CatchupPolicy =
        match s.ToLowerInvariant() with
        | "skip" -> SkipMissed
        | "once" -> CatchUpOnce
        | _ -> failwithf "unknown catchup policy: %s" s

    let catchupToString (c: CatchupPolicy) : string =
        match c with
        | SkipMissed -> "skip"
        | CatchUpOnce -> "once"

    let statusOfString (s: string) : ScheduleStatus =
        match ScheduleJobs.statusOfString s with
        | Some st -> st
        | None -> failwithf "unknown schedule status: %s" s

    let selectColumns =
        "id, user_id, chat_id, cron_expr, interval_seconds, timezone, prompt, catchup_policy, status, next_run, last_run_at, last_error, created_at, updated_at, after_seconds"

    let readJob (reader: SqliteDataReader) : ScheduleJob =
        let id = reader.GetInt64 0
        let uid = UserId(reader.GetInt64 1)
        let cid = ChatId(reader.GetInt64 2)
        let cron = if reader.IsDBNull 3 then None else Some(reader.GetString 3)
        let interval = if reader.IsDBNull 4 then None else Some(reader.GetInt32 4)
        let tz = reader.GetString 5
        let prompt = reader.GetString 6
        let catchup = catchupOfString (reader.GetString 7)
        let status = statusOfString (reader.GetString 8)

        let nextRun =
            if reader.IsDBNull 9 then
                None
            else
                Some(fromUnix (reader.GetInt64 9))

        let lastRunAt =
            if reader.IsDBNull 10 then
                None
            else
                Some(fromUnix (reader.GetInt64 10))

        let lastError =
            if reader.IsDBNull 11 then
                None
            else
                Some(reader.GetString 11)

        let createdAt = fromUnix (reader.GetInt64 12)
        let updatedAt = fromUnix (reader.GetInt64 13)

        let after =
            if reader.IsDBNull 14 then
                None
            else
                Some(reader.GetInt32 14)

        { Id = id
          UserId = uid
          ChatId = cid
          Prompt = prompt
          CronExpr = cron
          IntervalSeconds = interval
          AfterSeconds = after
          Timezone = tz
          Catchup = catchup
          Status = status
          NextRun = nextRun
          LastRunAt = lastRunAt
          LastError = lastError
          CreatedAt = createdAt
          UpdatedAt = updatedAt }

    let selectJobById (conn: SqliteConnection) (id: int64) : ScheduleJob option =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sprintf "SELECT %s FROM schedule_jobs WHERE id = $id;" selectColumns
        cmd.Parameters.AddWithValue("$id", id) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then Some(readJob reader) else None

    let readDueJob (conn: SqliteConnection) (tx: SqliteTransaction) (now: DateTimeOffset) : ScheduleJob option =
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx

        cmd.CommandText <-
            sprintf
                "SELECT %s FROM schedule_jobs WHERE status = 'active' AND next_run IS NOT NULL AND next_run <= $now ORDER BY next_run ASC, id ASC LIMIT 1;"
                selectColumns

        cmd.Parameters.AddWithValue("$now", toUnix now) |> ignore
        use reader = cmd.ExecuteReader()
        if reader.Read() then Some(readJob reader) else None

    interface IScheduleJobRepository with
        member _.Insert (draft: ScheduleJobDraft) (originToolCallId: string option) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "INSERT INTO schedule_jobs(user_id, chat_id, cron_expr, interval_seconds, after_seconds, timezone, prompt, catchup_policy, status, next_run, origin_tool_call_id, created_at, updated_at) VALUES ($userId, $chatId, $cron, $interval, $after, $timezone, $prompt, $catchup, 'pending', NULL, $origin, $now, $now) RETURNING id;"

                cmd.Parameters.AddWithValue("$userId", userId draft.UserId) |> ignore
                cmd.Parameters.AddWithValue("$chatId", chatId draft.ChatId) |> ignore
                addOpt cmd "$cron" draft.CronExpr
                addOpt cmd "$interval" draft.IntervalSeconds
                addOpt cmd "$after" draft.AfterSeconds
                cmd.Parameters.AddWithValue("$timezone", draft.Timezone) |> ignore
                cmd.Parameters.AddWithValue("$prompt", draft.Prompt) |> ignore
                cmd.Parameters.AddWithValue("$catchup", catchupToString draft.Catchup) |> ignore
                addOpt cmd "$origin" originToolCallId

                let nowUnix = toUnix DateTimeOffset.UtcNow
                cmd.Parameters.AddWithValue("$now", nowUnix) |> ignore

                match cmd.ExecuteScalar() with
                | :? int64 as id ->
                    { Id = id
                      UserId = draft.UserId
                      ChatId = draft.ChatId
                      Prompt = draft.Prompt
                      CronExpr = draft.CronExpr
                      IntervalSeconds = draft.IntervalSeconds
                      AfterSeconds = draft.AfterSeconds
                      Timezone = draft.Timezone
                      Catchup = draft.Catchup
                      Status = ScheduleStatus.Pending
                      NextRun = None
                      LastRunAt = None
                      LastError = None
                      CreatedAt = fromUnix nowUnix
                      UpdatedAt = fromUnix nowUnix }
                | _ -> failwith "schedule_jobs.Insert: could not resolve id")

        member _.FindByToolCallId(id: string) =
            exec.ReadAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    sprintf "SELECT %s FROM schedule_jobs WHERE origin_tool_call_id = $id;" selectColumns

                cmd.Parameters.AddWithValue("$id", id) |> ignore
                use reader = cmd.ExecuteReader()
                if reader.Read() then Some(readJob reader) else None)

        member _.GetById(id: int64) =
            exec.ReadAsync(fun conn -> selectJobById conn id)

        member _.ListForUser(uid: UserId) =
            exec.ReadAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    sprintf "SELECT %s FROM schedule_jobs WHERE user_id = $userId ORDER BY id ASC;" selectColumns

                cmd.Parameters.AddWithValue("$userId", userId uid) |> ignore
                use reader = cmd.ExecuteReader()
                let jobs = ResizeArray<ScheduleJob>()

                while reader.Read() do
                    jobs.Add(readJob reader)

                List.ofSeq jobs)

        member _.CountActiveForUser(uid: UserId) =
            exec.ReadAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <- "SELECT COUNT(*) FROM schedule_jobs WHERE user_id = $userId AND status = 'active';"

                cmd.Parameters.AddWithValue("$userId", userId uid) |> ignore
                cmd.ExecuteScalar() :?> int64 |> int)

        member _.Confirm(id: int64) =
            exec.WriteAsync(fun conn ->
                match selectJobById conn id with
                | Some job when job.Status = ScheduleStatus.Pending ->
                    let now = DateTimeOffset.UtcNow

                    let draft =
                        { UserId = job.UserId
                          ChatId = job.ChatId
                          Prompt = job.Prompt
                          CronExpr = job.CronExpr
                          IntervalSeconds = job.IntervalSeconds
                          AfterSeconds = job.AfterSeconds
                          Timezone = job.Timezone
                          Catchup = job.Catchup }

                    let nextRun = ScheduleJobs.nextRunAfter draft now

                    use upd = conn.CreateCommand()

                    upd.CommandText <-
                        "UPDATE schedule_jobs SET status = 'active', next_run = $nextRun, updated_at = $now WHERE id = $id;"

                    addOpt upd "$nextRun" (nextRun |> Option.map toUnix)
                    upd.Parameters.AddWithValue("$now", toUnix now) |> ignore
                    upd.Parameters.AddWithValue("$id", id) |> ignore
                    upd.ExecuteNonQuery() |> ignore
                    ()
                | _ -> ())

        member _.Cancel(id: int64) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "UPDATE schedule_jobs SET status = 'cancelled', updated_at = $now WHERE id = $id AND status = 'pending';"

                cmd.Parameters.AddWithValue("$now", toUnix DateTimeOffset.UtcNow) |> ignore
                cmd.Parameters.AddWithValue("$id", id) |> ignore
                cmd.ExecuteNonQuery() |> ignore
                ())

        member _.Pause(id: int64) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "UPDATE schedule_jobs SET status = 'paused', updated_at = $now WHERE id = $id AND status = 'active';"

                cmd.Parameters.AddWithValue("$now", toUnix DateTimeOffset.UtcNow) |> ignore
                cmd.Parameters.AddWithValue("$id", id) |> ignore
                cmd.ExecuteNonQuery() |> ignore
                ())

        member _.Resume(id: int64) =
            exec.WriteAsync(fun conn ->
                match selectJobById conn id with
                | Some job when job.Status = ScheduleStatus.Paused ->
                    let now = DateTimeOffset.UtcNow

                    let draft =
                        { UserId = job.UserId
                          ChatId = job.ChatId
                          Prompt = job.Prompt
                          CronExpr = job.CronExpr
                          IntervalSeconds = job.IntervalSeconds
                          AfterSeconds = job.AfterSeconds
                          Timezone = job.Timezone
                          Catchup = job.Catchup }

                    let nextRun = ScheduleJobs.nextRunAfter draft now

                    use upd = conn.CreateCommand()

                    upd.CommandText <-
                        "UPDATE schedule_jobs SET status = 'active', next_run = $nextRun, updated_at = $now WHERE id = $id;"

                    addOpt upd "$nextRun" (nextRun |> Option.map toUnix)
                    upd.Parameters.AddWithValue("$now", toUnix now) |> ignore
                    upd.Parameters.AddWithValue("$id", id) |> ignore
                    upd.ExecuteNonQuery() |> ignore
                    ()
                | _ -> ())

        member _.Remove(id: int64) =
            exec.WriteAsync(fun conn ->
                use tx = conn.BeginTransaction()
                use d1 = conn.CreateCommand()
                d1.Transaction <- tx
                d1.CommandText <- "DELETE FROM schedule_runs WHERE job_id = $jobId;"
                d1.Parameters.AddWithValue("$jobId", id) |> ignore
                d1.ExecuteNonQuery() |> ignore
                use d2 = conn.CreateCommand()
                d2.Transaction <- tx
                d2.CommandText <- "DELETE FROM schedule_jobs WHERE id = $id;"
                d2.Parameters.AddWithValue("$id", id) |> ignore
                d2.ExecuteNonQuery() |> ignore
                tx.Commit()
                ())

        member _.RunNow(id: int64) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "UPDATE schedule_jobs SET next_run = $now, updated_at = $now WHERE id = $id AND status = 'active';"

                cmd.Parameters.AddWithValue("$now", toUnix DateTimeOffset.UtcNow) |> ignore
                cmd.Parameters.AddWithValue("$id", id) |> ignore
                cmd.ExecuteNonQuery() |> ignore
                ())

        member _.ExpirePending (now: DateTimeOffset) (olderThan: DateTimeOffset) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "UPDATE schedule_jobs SET status = 'expired', updated_at = $now WHERE status = 'pending' AND created_at <= $olderThan;"

                cmd.Parameters.AddWithValue("$now", toUnix now) |> ignore
                cmd.Parameters.AddWithValue("$olderThan", toUnix olderThan) |> ignore
                cmd.ExecuteNonQuery())

        member _.PauseDueToErrors (id: int64) (err: string) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "UPDATE schedule_jobs SET status = 'paused', last_error = $err, updated_at = $now WHERE id = $id AND status = 'active';"

                cmd.Parameters.AddWithValue("$err", err) |> ignore
                cmd.Parameters.AddWithValue("$now", toUnix DateTimeOffset.UtcNow) |> ignore
                cmd.Parameters.AddWithValue("$id", id) |> ignore
                cmd.ExecuteNonQuery() |> ignore
                ())

        member _.ClaimDueOccurrence(now: DateTimeOffset) =
            exec.WriteAsync(fun conn ->
                use tx = conn.BeginTransaction()

                match readDueJob conn tx now with
                | None ->
                    tx.Rollback()
                    None
                | Some job ->
                    let scheduled = job.NextRun.Value
                    let payload = "[по расписанию]\n" + job.Prompt

                    let enqueueCommand () =
                        let externalKey = sprintf "sched:%d:%d" job.Id scheduled.UtcTicks
                        use ins = conn.CreateCommand()
                        ins.Transaction <- tx

                        ins.CommandText <-
                            """
                            INSERT INTO command_inbox(origin, external_key, user_id, chat_id, payload, priority, images, status, created_at, updated_at)
                            VALUES ($origin, $externalKey, $userId, $chatId, $payload, $priority, $images, 'pending', $now, $now)
                            ON CONFLICT(external_key) DO NOTHING
                            RETURNING id;
                        """

                        ins.Parameters.AddWithValue("$origin", "schedule") |> ignore
                        addOpt ins "$externalKey" (Some externalKey)
                        ins.Parameters.AddWithValue("$userId", userId job.UserId) |> ignore
                        ins.Parameters.AddWithValue("$chatId", chatId job.ChatId) |> ignore
                        ins.Parameters.AddWithValue("$payload", payload) |> ignore
                        ins.Parameters.AddWithValue("$priority", 10) |> ignore
                        ins.Parameters.AddWithValue("$images", "") |> ignore
                        ins.Parameters.AddWithValue("$now", toUnix now) |> ignore

                        match ins.ExecuteScalar() with
                        | :? int64 as commandId ->
                            use run = conn.CreateCommand()
                            run.Transaction <- tx

                            run.CommandText <-
                                "INSERT INTO schedule_runs(job_id, scheduled_for, status, command_id) VALUES ($jobId, $scheduledFor, 'claimed', $commandId) ON CONFLICT(job_id, scheduled_for) DO NOTHING;"

                            run.Parameters.AddWithValue("$jobId", job.Id) |> ignore
                            run.Parameters.AddWithValue("$scheduledFor", toUnix scheduled) |> ignore
                            run.Parameters.AddWithValue("$commandId", commandId) |> ignore
                            run.ExecuteNonQuery() |> ignore
                            Some commandId
                        | _ -> None

                    if job.AfterSeconds.IsSome then
                        // One-shot: fires once (even when late — the user asked "in 5
                        // minutes", so delivering at +6min after a restart is correct),
                        // then the job auto-completes without advancing next_run.
                        match enqueueCommand () with
                        | Some commandId ->
                            use upd = conn.CreateCommand()
                            upd.Transaction <- tx

                            upd.CommandText <-
                                "UPDATE schedule_jobs SET status = 'completed', last_run_at = $now, updated_at = $now WHERE id = $id;"

                            upd.Parameters.AddWithValue("$now", toUnix now) |> ignore
                            upd.Parameters.AddWithValue("$id", job.Id) |> ignore
                            upd.ExecuteNonQuery() |> ignore
                            tx.Commit()

                            Some
                                { Job =
                                    { job with
                                        Status = ScheduleStatus.Completed
                                        NextRun = None
                                        LastRunAt = Some now
                                        UpdatedAt = now }
                                  ScheduledFor = scheduled
                                  CommandId = commandId }
                        | None ->
                            tx.Rollback()
                            None
                    else
                        let policy = duePolicy job.Catchup
                        let due = isDue policy now job.LastRunAt scheduled

                        let nextRun =
                            ScheduleJobs.nextOccurrences job.CronExpr job.IntervalSeconds job.Timezone scheduled 1
                            |> List.tryHead

                        let advanceNextRun () =
                            use upd = conn.CreateCommand()
                            upd.Transaction <- tx

                            upd.CommandText <-
                                "UPDATE schedule_jobs SET next_run = $nextRun, updated_at = $now WHERE id = $id;"

                            addOpt upd "$nextRun" (nextRun |> Option.map toUnix)
                            upd.Parameters.AddWithValue("$now", toUnix now) |> ignore
                            upd.Parameters.AddWithValue("$id", job.Id) |> ignore
                            upd.ExecuteNonQuery() |> ignore
                            tx.Commit()

                        if not due then
                            advanceNextRun ()
                            None
                        else
                            match enqueueCommand () with
                            | Some commandId ->
                                use upd = conn.CreateCommand()
                                upd.Transaction <- tx

                                upd.CommandText <-
                                    "UPDATE schedule_jobs SET next_run = $nextRun, last_run_at = $now, updated_at = $now WHERE id = $id;"

                                addOpt upd "$nextRun" (nextRun |> Option.map toUnix)
                                upd.Parameters.AddWithValue("$now", toUnix now) |> ignore
                                upd.Parameters.AddWithValue("$id", job.Id) |> ignore
                                upd.ExecuteNonQuery() |> ignore
                                tx.Commit()

                                Some
                                    { Job =
                                        { job with
                                            NextRun = nextRun
                                            LastRunAt = Some now
                                            UpdatedAt = now }
                                      ScheduledFor = scheduled
                                      CommandId = commandId }
                            | None ->
                                advanceNextRun ()
                                None)
