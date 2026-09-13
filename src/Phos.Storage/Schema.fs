module Phos.Storage.Schema

open System
open System.Threading.Tasks

/// A single versioned schema migration step.
type Migration =
    { Version: int
      Name: string
      Sql: string }

/// Applies every migration in `migrations` with a version greater than the
/// current `schema_version`, each inside its own transaction. Idempotent:
/// already-applied migrations are skipped. Returns the number of migrations
/// applied in this run.
///
/// The storage executor performs work synchronously (Microsoft.Data.Sqlite has
/// no true async I/O), so the migrations are applied by blocking on the
/// completed tasks and the function returns an already-completed task.
let migrate (exec: StorageExecutor) (migrations: Migration list) : Task<int> =
    exec.WriteAsync(fun conn ->
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            "CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL, applied_at INTEGER NOT NULL);"

        cmd.ExecuteNonQuery() |> ignore
        ())
    |> fun t -> t.GetAwaiter().GetResult()

    let currentVersion =
        exec.ReadAsync(fun conn ->
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COALESCE(MAX(version), 0) FROM schema_version;"

            match cmd.ExecuteScalar() with
            | :? int64 as v -> int v
            | :? int as v -> v
            | _ -> 0)
        |> fun t -> t.GetAwaiter().GetResult()

    let pending =
        migrations
        |> List.filter (fun m -> m.Version > currentVersion)
        |> List.sortBy (fun m -> m.Version)

    let mutable applied = 0

    for m in pending do
        exec.WriteAsync(fun conn ->
            use tx = conn.BeginTransaction()
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- m.Sql
            cmd.ExecuteNonQuery() |> ignore
            use cmd2 = conn.CreateCommand()
            cmd2.Transaction <- tx
            cmd2.CommandText <- "INSERT INTO schema_version(version, applied_at) VALUES ($version, $now);"

            cmd2.Parameters.AddWithValue("$version", m.Version) |> ignore

            cmd2.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            |> ignore

            cmd2.ExecuteNonQuery() |> ignore
            tx.Commit()
            ())
        |> fun t -> t.GetAwaiter().GetResult()

        applied <- applied + 1

    Task.FromResult applied

/// The full v2 schema, split into versioned steps so an older database can be
/// upgraded by replaying the migrations above its current `schema_version`.
let migrations: Migration list =
    [ { Version = 1
        Name = "users"
        Sql =
          "CREATE TABLE users (user_id INTEGER PRIMARY KEY, username TEXT, role TEXT NOT NULL, workspace_path TEXT NOT NULL, timezone TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);" }
      { Version = 2
        Name = "command_inbox"
        Sql =
          "CREATE TABLE command_inbox (id INTEGER PRIMARY KEY AUTOINCREMENT, origin TEXT NOT NULL, external_key TEXT UNIQUE, user_id INTEGER NOT NULL, chat_id INTEGER NOT NULL, payload TEXT NOT NULL, priority INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT 'pending', lease_until INTEGER NULL, heartbeat_at INTEGER NULL, attempts INTEGER NOT NULL DEFAULT 0, max_attempts INTEGER NOT NULL DEFAULT 5, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);" }
      { Version = 3
        Name = "message_outbox"
        Sql =
          "CREATE TABLE message_outbox (id INTEGER PRIMARY KEY AUTOINCREMENT, command_id INTEGER NOT NULL, chunk_index INTEGER NOT NULL, chat_id INTEGER NOT NULL, random_id INTEGER NOT NULL UNIQUE, payload TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'pending', remote_message_id INTEGER NULL, attempts INTEGER NOT NULL DEFAULT 0, max_attempts INTEGER NOT NULL DEFAULT 5, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, UNIQUE(command_id, chunk_index));" }
      { Version = 4
        Name = "schedule_jobs"
        Sql =
          "CREATE TABLE schedule_jobs (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INTEGER NOT NULL, cron_expr TEXT NOT NULL, timezone TEXT NOT NULL, prompt TEXT NOT NULL, enabled INTEGER NOT NULL DEFAULT 1, catchup_policy TEXT NOT NULL DEFAULT 'skip', next_run INTEGER NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);" }
      { Version = 5
        Name = "schedule_runs"
        Sql =
          "CREATE TABLE schedule_runs (job_id INTEGER NOT NULL, scheduled_for INTEGER NOT NULL, status TEXT NOT NULL, command_id INTEGER NULL, PRIMARY KEY(job_id, scheduled_for));" }
      { Version = 6
        Name = "backup_log"
        Sql =
          "CREATE TABLE backup_log (id INTEGER PRIMARY KEY AUTOINCREMENT, started_at INTEGER NOT NULL, finished_at INTEGER NULL, path TEXT NULL, checksum TEXT NULL, status TEXT NOT NULL, error TEXT NULL);" } ]
