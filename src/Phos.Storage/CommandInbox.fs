namespace Phos.Storage

open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Phos.Core
open Phos.Core.DomainTypes
open Phos.Core.InboxStateMachine

/// A command waiting in the inbox before execution.
type CommandEnvelope =
    { Origin: Origin
      ExternalKey: string option
      UserId: UserId
      ChatId: ChatId
      Payload: string
      Priority: int }

/// A claim lease for a command.
type Lease =
    { Until: DateTimeOffset
      HeartbeatAt: DateTimeOffset }

/// A durable inbox command with its execution state.
type Command =
    { Id: int64
      Envelope: CommandEnvelope
      Status: InboxStateMachine.Status
      Attempts: int
      MaxAttempts: int
      LeaseUntil: DateTimeOffset option
      HeartbeatAt: DateTimeOffset option }

/// Repository for the `command_inbox` durable queue.
type ICommandInbox =
    abstract Insert: CommandEnvelope -> Task<int64>
    abstract ClaimNextForChat: ChatId -> Lease -> Task<Command option>
    abstract ClaimById: int64 -> Lease -> Task<Command option>
    abstract GetById: int64 -> Task<Command option>
    abstract MarkStarted: int64 -> Task<unit>
    abstract MarkCompleted: int64 -> Task<unit>
    abstract MarkFailed: int64 -> Task<unit>
    abstract Retry: int64 -> Task<unit>
    abstract MarkNeedsReview: int64 -> Task<unit>
    abstract ReviewRetry: int64 -> Task<unit>
    abstract Heartbeat: int64 -> DateTimeOffset -> Task<unit>
    abstract ExpireLeases: DateTimeOffset -> Task<int>
    abstract CountPending: unit -> Task<int>
    abstract CountDeadLetter: unit -> Task<int>

type CommandInbox(exec: StorageExecutor) =
    let originToString (o: Origin) : string =
        match o with
        | Telegram -> "telegram"
        | Schedule -> "schedule"
        | System -> "system"

    let originOfString (s: string) : Origin =
        match s.ToLowerInvariant() with
        | "telegram" -> Telegram
        | "schedule" -> Schedule
        | _ -> System

    let statusOfString (s: string) : InboxStateMachine.Status =
        match s.ToLowerInvariant() with
        | "pending" -> Status.Pending
        | "claimed" -> Status.Claimed
        | "running" -> Status.Running
        | "completed" -> Status.Completed
        | "failed" -> Status.Failed
        | "dead_letter" -> Status.DeadLetter
        | "needs_review" -> Status.NeedsReview
        | _ -> failwithf "unknown inbox status: %s" s

    let userId (UserId id) = id
    let chatId (ChatId id) = id

    let toUnix (dto: DateTimeOffset) = dto.ToUnixTimeSeconds()

    let fromUnix (s: int64) = DateTimeOffset.FromUnixTimeSeconds s

    let addOpt (cmd: SqliteCommand) (name: string) (v: string option) =
        cmd.Parameters.AddWithValue(
            name,
            match v with
            | Some s -> box s
            | None -> box DBNull.Value
        )
        |> ignore

    let readCommand (reader: SqliteDataReader) : Command =
        let id = reader.GetInt64 0
        let origin = originOfString (reader.GetString 1)
        let externalKey = if reader.IsDBNull 2 then None else Some(reader.GetString 2)
        let uid = UserId(reader.GetInt64 3)
        let cid = ChatId(reader.GetInt64 4)
        let payload = reader.GetString 5
        let priority = reader.GetInt32 6
        let status = statusOfString (reader.GetString 7)
        let attempts = reader.GetInt32 8
        let maxAttempts = reader.GetInt32 9

        let leaseUntil =
            if reader.IsDBNull 10 then
                None
            else
                Some(fromUnix (reader.GetInt64 10))

        let heartbeatAt =
            if reader.IsDBNull 11 then
                None
            else
                Some(fromUnix (reader.GetInt64 11))

        { Id = id
          Envelope =
            { Origin = origin
              ExternalKey = externalKey
              UserId = uid
              ChatId = cid
              Payload = payload
              Priority = priority }
          Status = status
          Attempts = attempts
          MaxAttempts = maxAttempts
          LeaseUntil = leaseUntil
          HeartbeatAt = heartbeatAt }

    let selectCommandById (conn: SqliteConnection) (id: int64) : Command option =
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            """
            SELECT id, origin, external_key, user_id, chat_id, payload, priority, status, attempts, max_attempts, lease_until, heartbeat_at
            FROM command_inbox WHERE id = $id;
        """

        cmd.Parameters.AddWithValue("$id", id) |> ignore

        use reader = cmd.ExecuteReader()
        if reader.Read() then Some(readCommand reader) else None

    interface ICommandInbox with
        member _.Insert(env: CommandEnvelope) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    """
                    INSERT INTO command_inbox(origin, external_key, user_id, chat_id, payload, priority, status, created_at, updated_at)
                    VALUES ($origin, $externalKey, $userId, $chatId, $payload, $priority, 'pending', $now, $now)
                    ON CONFLICT(external_key) DO NOTHING
                    RETURNING id;
                """

                cmd.Parameters.AddWithValue("$origin", originToString env.Origin) |> ignore
                addOpt cmd "$externalKey" env.ExternalKey
                cmd.Parameters.AddWithValue("$userId", userId env.UserId) |> ignore
                cmd.Parameters.AddWithValue("$chatId", chatId env.ChatId) |> ignore
                cmd.Parameters.AddWithValue("$payload", env.Payload) |> ignore
                cmd.Parameters.AddWithValue("$priority", env.Priority) |> ignore

                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                |> ignore

                match cmd.ExecuteScalar() with
                | :? int64 as id -> id
                | _ ->
                    match env.ExternalKey with
                    | Some key ->
                        use sel = conn.CreateCommand()
                        sel.CommandText <- "SELECT id FROM command_inbox WHERE external_key = $externalKey;"
                        sel.Parameters.AddWithValue("$externalKey", key) |> ignore

                        match sel.ExecuteScalar() with
                        | :? int64 as id -> id
                        | _ -> failwith "command_inbox.Insert: could not resolve existing id"
                    | None -> failwith "command_inbox.Insert: unexpected conflict with null external key")

        member _.ClaimNextForChat (chat: ChatId) (lease: Lease) =
            exec.WriteAsync(fun conn ->
                use tx = conn.BeginTransaction()
                use sel = conn.CreateCommand()
                sel.Transaction <- tx

                sel.CommandText <-
                    """
                    SELECT id FROM command_inbox
                    WHERE chat_id = $chatId AND status = 'pending'
                    ORDER BY priority DESC, created_at ASC, id ASC
                    LIMIT 1;
                """

                sel.Parameters.AddWithValue("$chatId", chatId chat) |> ignore

                match sel.ExecuteScalar() with
                | :? int64 as id ->
                    use upd = conn.CreateCommand()
                    upd.Transaction <- tx

                    upd.CommandText <-
                        """
                        UPDATE command_inbox
                        SET status = 'claimed', lease_until = $leaseUntil, heartbeat_at = $heartbeatAt, updated_at = $now
                        WHERE id = $id AND status = 'pending';
                    """

                    upd.Parameters.AddWithValue("$leaseUntil", toUnix lease.Until) |> ignore
                    upd.Parameters.AddWithValue("$heartbeatAt", toUnix lease.HeartbeatAt) |> ignore

                    upd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    |> ignore

                    upd.Parameters.AddWithValue("$id", id) |> ignore

                    if upd.ExecuteNonQuery() = 1 then
                        tx.Commit()
                        selectCommandById conn id
                    else
                        tx.Rollback()
                        None
                | _ ->
                    tx.Rollback()
                    None)

        member _.ClaimById (id: int64) (lease: Lease) =
            exec.WriteAsync(fun conn ->
                use tx = conn.BeginTransaction()
                use upd = conn.CreateCommand()
                upd.Transaction <- tx

                upd.CommandText <-
                    """
                    UPDATE command_inbox
                    SET status = 'claimed', lease_until = $leaseUntil, heartbeat_at = $heartbeatAt, updated_at = $now
                    WHERE id = $id AND status = 'pending';
                """

                upd.Parameters.AddWithValue("$leaseUntil", toUnix lease.Until) |> ignore
                upd.Parameters.AddWithValue("$heartbeatAt", toUnix lease.HeartbeatAt) |> ignore

                upd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                |> ignore

                upd.Parameters.AddWithValue("$id", id) |> ignore

                if upd.ExecuteNonQuery() = 1 then
                    tx.Commit()
                    selectCommandById conn id
                else
                    tx.Rollback()
                    None)

        member _.GetById(id: int64) =
            exec.ReadAsync(fun conn -> selectCommandById conn id)

        member _.MarkStarted(id: int64) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "UPDATE command_inbox SET status = 'running', updated_at = $now WHERE id = $id AND status = 'claimed';"

                cmd.Parameters.AddWithValue("$id", id) |> ignore

                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                |> ignore

                cmd.ExecuteNonQuery() |> ignore
                ())

        member _.MarkCompleted(id: int64) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "UPDATE command_inbox SET status = 'completed', updated_at = $now WHERE id = $id AND status = 'running';"

                cmd.Parameters.AddWithValue("$id", id) |> ignore

                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                |> ignore

                cmd.ExecuteNonQuery() |> ignore
                ())

        member _.MarkFailed(id: int64) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    """
                    UPDATE command_inbox
                    SET attempts = attempts + 1,
                        status = CASE WHEN attempts + 1 >= max_attempts THEN 'dead_letter' ELSE 'failed' END,
                        updated_at = $now
                    WHERE id = $id;
                """

                cmd.Parameters.AddWithValue("$id", id) |> ignore

                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                |> ignore

                cmd.ExecuteNonQuery() |> ignore
                ())

        member _.Retry(id: int64) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "UPDATE command_inbox SET status = 'pending', lease_until = NULL, heartbeat_at = NULL, updated_at = $now WHERE id = $id AND status = 'failed';"

                cmd.Parameters.AddWithValue("$id", id) |> ignore

                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                |> ignore

                cmd.ExecuteNonQuery() |> ignore
                ())

        member _.MarkNeedsReview(id: int64) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "UPDATE command_inbox SET status = 'needs_review', updated_at = $now WHERE id = $id AND status IN ('claimed', 'running');"

                cmd.Parameters.AddWithValue("$id", id) |> ignore

                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                |> ignore

                cmd.ExecuteNonQuery() |> ignore
                ())

        member _.ReviewRetry(id: int64) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "UPDATE command_inbox SET status = 'pending', lease_until = NULL, heartbeat_at = NULL, updated_at = $now WHERE id = $id AND status = 'needs_review';"

                cmd.Parameters.AddWithValue("$id", id) |> ignore

                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                |> ignore

                cmd.ExecuteNonQuery() |> ignore
                ())

        member _.Heartbeat (id: int64) (at: DateTimeOffset) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "UPDATE command_inbox SET heartbeat_at = $heartbeatAt, updated_at = $now WHERE id = $id;"

                cmd.Parameters.AddWithValue("$heartbeatAt", toUnix at) |> ignore
                cmd.Parameters.AddWithValue("$id", id) |> ignore

                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                |> ignore

                cmd.ExecuteNonQuery() |> ignore
                ())

        member _.ExpireLeases(now: DateTimeOffset) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    """
                    UPDATE command_inbox
                    SET status = 'pending', lease_until = NULL, heartbeat_at = NULL, updated_at = $now
                    WHERE status IN ('claimed', 'running') AND lease_until < $now;
                """

                cmd.Parameters.AddWithValue("$now", toUnix now) |> ignore
                cmd.ExecuteNonQuery())

        member _.CountPending() =
            exec.ReadAsync(fun conn ->
                use cmd = conn.CreateCommand()
                cmd.CommandText <- "SELECT COUNT(*) FROM command_inbox WHERE status = 'pending';"
                cmd.ExecuteScalar() :?> int64 |> int)

        member _.CountDeadLetter() =
            exec.ReadAsync(fun conn ->
                use cmd = conn.CreateCommand()
                cmd.CommandText <- "SELECT COUNT(*) FROM command_inbox WHERE status = 'dead_letter';"
                cmd.ExecuteScalar() :?> int64 |> int)
