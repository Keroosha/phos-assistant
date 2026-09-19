namespace Phos.Storage

open System
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Phos.Core
open Phos.Core.DomainTypes
open Phos.Core.Chunker
open Phos.Core.OutboxStateMachine

/// A message entry in the transactional outbox.
type OutboxEntry =
    { Id: int64
      CommandId: int64
      ChunkIndex: int
      ChatId: ChatId
      RandomId: int64
      Payload: string
      Entities: Entity list
      Status: OutboxStateMachine.Status
      Attempts: int
      MaxAttempts: int
      RemoteMessageId: int64 option
      CreatedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset }

/// Repository for the `message_outbox` table.
type IMessageOutbox =
    abstract Insert:
        commandId: int64 ->
        chunkIndex: int ->
        chatId: ChatId ->
        randomId: int64 ->
        payload: string ->
        entities: Entity list ->
            Task<int64>

    abstract NextPending: unit -> Task<OutboxEntry option>
    abstract BeginSend: id: int64 -> Task<unit>
    abstract MarkSent: id: int64 -> remoteMessageId: int64 -> Task<unit>
    abstract MarkFailed: id: int64 -> Task<unit>
    abstract Retry: id: int64 -> Task<unit>
    abstract Defer: id: int64 -> availableAt: DateTimeOffset -> Task<unit>
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

    /// Encodes entities to the outbox's JSON text column. An empty list is
    /// stored as an empty string (round-trips back to `[]`).
    let encodeEntities (entities: Entity list) : string =
        if List.isEmpty entities then
            ""
        else
            let items =
                entities
                |> List.map (fun e ->
                    {| offset = e.Offset
                       length = e.Length
                       kind = e.Kind.ToString()
                       url = e.Url |> Option.toObj |})

            JsonSerializer.Serialize(items)

    /// Decodes the JSON text column back into entities. Missing/NULL/empty
    /// values become `[]`; any parse failure is treated as `[]` so a corrupt
    /// row never blocks delivery.
    let decodeEntities (s: string) : Entity list =
        if String.IsNullOrWhiteSpace s then
            []
        else
            try
                use doc = JsonDocument.Parse(s)

                [ for el in doc.RootElement.EnumerateArray() ->
                      let kind =
                          match el.GetProperty("kind").GetString() with
                          | "Bold" -> EntityKind.Bold
                          | "Italic" -> EntityKind.Italic
                          | "Code" -> EntityKind.Code
                          | "Pre" -> EntityKind.Pre
                          | "TextUrl" -> EntityKind.TextUrl
                          | "Mention" -> EntityKind.Mention
                          | "Hashtag" -> EntityKind.Hashtag
                          | _ -> EntityKind.Unknown

                      let url =
                          match el.TryGetProperty("url") with
                          | true, p when p.ValueKind = JsonValueKind.String ->
                              match p.GetString() with
                              | null -> None
                              | s -> Some s
                          | _ -> None

                      { Offset = el.GetProperty("offset").GetInt32()
                        Length = el.GetProperty("length").GetInt32()
                        Kind = kind
                        Url = url } ]
            with _ ->
                []

    let selectColumns =
        "id, command_id, chunk_index, chat_id, random_id, payload, entities, status, attempts, max_attempts, remote_message_id, created_at, updated_at"

    let readEntry (reader: SqliteDataReader) : OutboxEntry =
        let id = reader.GetInt64 0
        let commandId = reader.GetInt64 1
        let chunkIndex = reader.GetInt32 2
        let cid = ChatId(reader.GetInt64 3)
        let randomId = reader.GetInt64 4
        let payload = reader.GetString 5

        let entities = decodeEntities (if reader.IsDBNull 6 then "" else reader.GetString 6)

        let status = statusOfString (reader.GetString 7)
        let attempts = reader.GetInt32 8
        let maxAttempts = reader.GetInt32 9

        let remoteMessageId =
            if reader.IsDBNull 10 then
                None
            else
                Some(reader.GetInt64 10)

        let createdAt = fromUnix (reader.GetInt64 11)
        let updatedAt = fromUnix (reader.GetInt64 12)

        { Id = id
          CommandId = commandId
          ChunkIndex = chunkIndex
          ChatId = cid
          RandomId = randomId
          Payload = payload
          Entities = entities
          Status = status
          Attempts = attempts
          MaxAttempts = maxAttempts
          RemoteMessageId = remoteMessageId
          CreatedAt = createdAt
          UpdatedAt = updatedAt }

    interface IMessageOutbox with
        member _.Insert
            (commandId: int64)
            (chunkIndex: int)
            (chat: ChatId)
            (randomId: int64)
            (payload: string)
            (entities: Entity list)
            =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    """
                    INSERT INTO message_outbox(command_id, chunk_index, chat_id, random_id, payload, entities, status, created_at, updated_at)
                    VALUES ($commandId, $chunkIndex, $chatId, $randomId, $payload, $entities, 'pending', $now, $now)
                    ON CONFLICT DO NOTHING
                    RETURNING id;
                """

                cmd.Parameters.AddWithValue("$commandId", commandId) |> ignore
                cmd.Parameters.AddWithValue("$chunkIndex", chunkIndex) |> ignore
                cmd.Parameters.AddWithValue("$chatId", chatId chat) |> ignore
                cmd.Parameters.AddWithValue("$randomId", randomId) |> ignore
                cmd.Parameters.AddWithValue("$payload", payload) |> ignore
                cmd.Parameters.AddWithValue("$entities", encodeEntities entities) |> ignore

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
                    WHERE (status = 'pending' AND updated_at <= $now)
                       OR (status = 'failed' AND attempts < max_attempts)
                       OR (status = 'sending' AND updated_at <= $sendingStaleBefore)
                    ORDER BY id ASC
                    LIMIT 1;
                """
                        selectColumns

                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                |> ignore

                cmd.Parameters.AddWithValue("$sendingStaleBefore", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10L)
                |> ignore

                use reader = cmd.ExecuteReader()
                if reader.Read() then Some(readEntry reader) else None)

        member _.BeginSend(id: int64) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    """
                UPDATE message_outbox
                SET status = 'sending', updated_at = $now
                WHERE id = $id
                  AND ((status = 'pending' AND updated_at <= $now)
                    OR (status = 'failed' AND attempts < max_attempts)
                    OR (status = 'sending' AND updated_at <= $sendingStaleBefore));
                """

                cmd.Parameters.AddWithValue("$id", id) |> ignore

                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                |> ignore

                cmd.Parameters.AddWithValue("$sendingStaleBefore", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10L)
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
                    "UPDATE message_outbox SET status = 'pending', updated_at = $now WHERE id = $id AND status = 'failed' AND attempts < max_attempts;"

                cmd.Parameters.AddWithValue("$id", id) |> ignore

                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                |> ignore

                cmd.ExecuteNonQuery() |> ignore
                ())

        member _.Defer (id: int64) (availableAt: DateTimeOffset) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "UPDATE message_outbox SET status = 'pending', updated_at = $availableAt WHERE id = $id AND (status = 'sending' OR (status = 'failed' AND attempts < max_attempts));"

                cmd.Parameters.AddWithValue("$id", id) |> ignore
                cmd.Parameters.AddWithValue("$availableAt", toUnix availableAt) |> ignore

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
