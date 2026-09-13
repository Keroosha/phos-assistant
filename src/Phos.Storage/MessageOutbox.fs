namespace Phos.Storage

open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Phos.Core
open Phos.Core.DomainTypes
open Phos.Core.OutboxStateMachine

/// A message entry in the transactional outbox.
type OutboxEntry =
    { Id: int64
      CommandId: int64
      ChunkIndex: int
      ChatId: ChatId
      RandomId: int64
      Payload: string
      Status: OutboxStateMachine.Status
      Attempts: int
      MaxAttempts: int
      RemoteMessageId: int64 option
      CreatedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset }

/// Repository for the `message_outbox` table.
type IMessageOutbox =
    abstract Insert:
        commandId: int64 -> chunkIndex: int -> chatId: ChatId -> randomId: int64 -> payload: string -> Task<int64>

    abstract NextPending: unit -> Task<OutboxEntry option>
    abstract BeginSend: id: int64 -> Task<unit>
    abstract MarkSent: id: int64 -> remoteMessageId: int64 -> Task<unit>
    abstract MarkFailed: id: int64 -> Task<unit>
    abstract Retry: id: int64 -> Task<unit>
    abstract GetByRandomId: randomId: int64 -> Task<OutboxEntry option>
    abstract CountPending: unit -> Task<int>

type MessageOutbox(exec: StorageExecutor) =
    let chatId (ChatId id) = id

    let toUnix (dto: DateTimeOffset) = dto.ToUnixTimeSeconds()

    let fromUnix (s: int64) = DateTimeOffset.FromUnixTimeSeconds s

    let statusOfString (s: string) : OutboxStateMachine.Status =
        match s.ToLowerInvariant() with
        | "pending" -> Status.Pending
        | "sending" -> Status.Sending
        | "sent" -> Status.Sent
        | "failed" -> Status.Failed
        | _ -> failwithf "unknown outbox status: %s" s

    let selectColumns =
        "id, command_id, chunk_index, chat_id, random_id, payload, status, attempts, max_attempts, remote_message_id, created_at, updated_at"

    let readEntry (reader: SqliteDataReader) : OutboxEntry =
        let id = reader.GetInt64 0
        let commandId = reader.GetInt64 1
        let chunkIndex = reader.GetInt32 2
        let cid = ChatId(reader.GetInt64 3)
        let randomId = reader.GetInt64 4
        let payload = reader.GetString 5
        let status = statusOfString (reader.GetString 6)
        let attempts = reader.GetInt32 7
        let maxAttempts = reader.GetInt32 8
        let remoteMessageId = if reader.IsDBNull 9 then None else Some(reader.GetInt64 9)
        let createdAt = fromUnix (reader.GetInt64 10)
        let updatedAt = fromUnix (reader.GetInt64 11)

        { Id = id
          CommandId = commandId
          ChunkIndex = chunkIndex
          ChatId = cid
          RandomId = randomId
          Payload = payload
          Status = status
          Attempts = attempts
          MaxAttempts = maxAttempts
          RemoteMessageId = remoteMessageId
          CreatedAt = createdAt
          UpdatedAt = updatedAt }

    interface IMessageOutbox with
        member _.Insert (commandId: int64) (chunkIndex: int) (chat: ChatId) (randomId: int64) (payload: string) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    """
                    INSERT INTO message_outbox(command_id, chunk_index, chat_id, random_id, payload, status, created_at, updated_at)
                    VALUES ($commandId, $chunkIndex, $chatId, $randomId, $payload, 'pending', $now, $now)
                    ON CONFLICT DO NOTHING
                    RETURNING id;
                """

                cmd.Parameters.AddWithValue("$commandId", commandId) |> ignore
                cmd.Parameters.AddWithValue("$chunkIndex", chunkIndex) |> ignore
                cmd.Parameters.AddWithValue("$chatId", chatId chat) |> ignore
                cmd.Parameters.AddWithValue("$randomId", randomId) |> ignore
                cmd.Parameters.AddWithValue("$payload", payload) |> ignore

                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                |> ignore

                match cmd.ExecuteScalar() with
                | :? int64 as id -> id
                | _ ->
                    // Conflict on random_id or (command_id, chunk_index): return the existing id.
                    use sel = conn.CreateCommand()

                    sel.CommandText <-
                        """
                        SELECT id FROM message_outbox
                        WHERE random_id = $randomId OR (command_id = $commandId AND chunk_index = $chunkIndex)
                        ORDER BY CASE WHEN random_id = $randomId THEN 0 ELSE 1 END
                        LIMIT 1;
                    """

                    sel.Parameters.AddWithValue("$randomId", randomId) |> ignore
                    sel.Parameters.AddWithValue("$commandId", commandId) |> ignore
                    sel.Parameters.AddWithValue("$chunkIndex", chunkIndex) |> ignore

                    match sel.ExecuteScalar() with
                    | :? int64 as id -> id
                    | _ -> failwith "message_outbox.Insert: could not resolve existing id")

        member _.NextPending() =
            exec.ReadAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    sprintf
                        """
                    SELECT %s FROM message_outbox
                    WHERE status = 'pending' OR (status = 'failed' AND attempts < max_attempts)
                    ORDER BY id ASC
                    LIMIT 1;
                """
                        selectColumns

                use reader = cmd.ExecuteReader()
                if reader.Read() then Some(readEntry reader) else None)

        member _.BeginSend(id: int64) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    """
                    UPDATE message_outbox
                    SET status = 'sending', updated_at = $now
                    WHERE id = $id AND (status = 'pending' OR (status = 'failed' AND attempts < max_attempts));
                """

                cmd.Parameters.AddWithValue("$id", id) |> ignore

                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                |> ignore

                cmd.ExecuteNonQuery() |> ignore
                ())

        member _.MarkSent (id: int64) (remoteMessageId: int64) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "UPDATE message_outbox SET status = 'sent', remote_message_id = $remoteMessageId, updated_at = $now WHERE id = $id AND status = 'sending';"

                cmd.Parameters.AddWithValue("$remoteMessageId", remoteMessageId) |> ignore
                cmd.Parameters.AddWithValue("$id", id) |> ignore

                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                |> ignore

                cmd.ExecuteNonQuery() |> ignore
                ())

        member _.MarkFailed(id: int64) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "UPDATE message_outbox SET status = 'failed', attempts = attempts + 1, updated_at = $now WHERE id = $id AND status = 'sending';"

                cmd.Parameters.AddWithValue("$id", id) |> ignore

                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                |> ignore

                cmd.ExecuteNonQuery() |> ignore
                ())

        member _.Retry(id: int64) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "UPDATE message_outbox SET status = 'pending', updated_at = $now WHERE id = $id AND status = 'failed';"

                cmd.Parameters.AddWithValue("$id", id) |> ignore

                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                |> ignore

                cmd.ExecuteNonQuery() |> ignore
                ())

        member _.GetByRandomId(randomId: int64) =
            exec.ReadAsync(fun conn ->
                use cmd = conn.CreateCommand()
                cmd.CommandText <- sprintf "SELECT %s FROM message_outbox WHERE random_id = $randomId;" selectColumns
                cmd.Parameters.AddWithValue("$randomId", randomId) |> ignore

                use reader = cmd.ExecuteReader()
                if reader.Read() then Some(readEntry reader) else None)

        member _.CountPending() =
            exec.ReadAsync(fun conn ->
                use cmd = conn.CreateCommand()
                cmd.CommandText <- "SELECT COUNT(*) FROM message_outbox WHERE status = 'pending';"
                cmd.ExecuteScalar() :?> int64 |> int)
