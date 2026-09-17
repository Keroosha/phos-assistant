namespace Phos.Storage

open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite

/// One row of the `backup_log` table: a record of a backup attempt and its
/// outcome. A backup that is still running has `Status = "running"` and a
/// `None` `FinishedAt`; on completion the row is updated with `"ok"` or
/// `"failed"` plus the optional archive path, checksum and error message.
type BackupLogEntry =
    { Id: int64
      StartedAt: DateTimeOffset
      FinishedAt: DateTimeOffset option
      Path: string option
      Checksum: string option
      Status: string
      Error: string option }

/// Durable repository for backup run logs.
type IBackupLogRepository =
    abstract Start: startedAt: DateTimeOffset -> Task<int64>

    abstract Finish:
        id: int64 ->
        finishedAt: DateTimeOffset ->
        path: string option ->
        checksum: string option ->
        status: string ->
        error: string option ->
            Task<unit>

    abstract ListRecent: limit: int -> Task<BackupLogEntry list>

type BackupLogRepository(exec: StorageExecutor) =
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

    let readEntry (reader: SqliteDataReader) : BackupLogEntry =
        let id = reader.GetInt64 0
        let startedAt = fromUnix (reader.GetInt64 1)

        let finishedAt =
            if reader.IsDBNull 2 then
                None
            else
                Some(fromUnix (reader.GetInt64 2))

        let path = if reader.IsDBNull 3 then None else Some(reader.GetString 3)

        let checksum = if reader.IsDBNull 4 then None else Some(reader.GetString 4)

        let status = reader.GetString 5

        let error = if reader.IsDBNull 6 then None else Some(reader.GetString 6)

        { Id = id
          StartedAt = startedAt
          FinishedAt = finishedAt
          Path = path
          Checksum = checksum
          Status = status
          Error = error }

    interface IBackupLogRepository with
        member _.Start(startedAt: DateTimeOffset) =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "INSERT INTO backup_log(started_at, status) VALUES ($startedAt, 'running') RETURNING id;"

                cmd.Parameters.AddWithValue("$startedAt", toUnix startedAt) |> ignore

                match cmd.ExecuteScalar() with
                | :? int64 as id -> id
                | _ -> failwith "backup_log.Start: could not resolve id")

        member _.Finish
            (id: int64)
            (finishedAt: DateTimeOffset)
            (path: string option)
            (checksum: string option)
            (status: string)
            (error: string option)
            =
            exec.WriteAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "UPDATE backup_log SET finished_at = $finishedAt, path = $path, checksum = $checksum, status = $status, error = $error WHERE id = $id;"

                cmd.Parameters.AddWithValue("$finishedAt", toUnix finishedAt) |> ignore
                addOpt cmd "$path" path
                addOpt cmd "$checksum" checksum
                cmd.Parameters.AddWithValue("$status", status) |> ignore
                addOpt cmd "$error" error
                cmd.Parameters.AddWithValue("$id", id) |> ignore
                cmd.ExecuteNonQuery() |> ignore
                ())

        member _.ListRecent(limit: int) =
            exec.ReadAsync(fun conn ->
                use cmd = conn.CreateCommand()

                cmd.CommandText <-
                    "SELECT id, started_at, finished_at, path, checksum, status, error FROM backup_log ORDER BY id DESC LIMIT $limit;"

                cmd.Parameters.AddWithValue("$limit", limit) |> ignore
                use reader = cmd.ExecuteReader()
                let entries = ResizeArray<BackupLogEntry>()

                while reader.Read() do
                    entries.Add(readEntry reader)

                List.ofSeq entries)
