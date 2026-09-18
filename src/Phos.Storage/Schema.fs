module Phos.Storage.Schema

open System
open FluentMigrator
open FluentMigrator.Runner
open Microsoft.Data.Sqlite
open Microsoft.Extensions.DependencyInjection

// ---------------------------------------------------------------------------
// Migrations
//
// The schema is split into six versioned steps (v1..v6) so an existing
// database can be upgraded by replaying the steps above its current version.
// Each migration is a FluentMigrator step that creates (or drops) one table;
// FluentMigrator tracks the applied version in its `VersionInfo` table, so
// re-running `run` is idempotent.
//
// Column nullability is explicit (`.Nullable()` / `.NotNullable()`) because
// FluentMigrator defaults to NOT NULL, which would otherwise silently change
// nullable columns (e.g. `users.username`, `command_inbox.external_key`).
// The composite `UNIQUE(command_id, chunk_index)` and `PRIMARY KEY(job_id,
// scheduled_for)` are expressed as unique indexes because SQLite cannot add
// a table-level constraint to an existing table via `ALTER TABLE`.
// ---------------------------------------------------------------------------

[<Migration(1L)>]
type CreateUsers() =
    inherit Migration()

    override this.Up() =
        this.Create
            .Table("users")
            .WithColumn("user_id")
            .AsInt64()
            .PrimaryKey()
            .WithColumn("username")
            .AsString()
            .Nullable()
            .WithColumn("role")
            .AsString()
            .NotNullable()
            .WithColumn("workspace_path")
            .AsString()
            .NotNullable()
            .WithColumn("timezone")
            .AsString()
            .Nullable()
            .WithColumn("created_at")
            .AsInt64()
            .NotNullable()
            .WithColumn("updated_at")
            .AsInt64()
            .NotNullable()
        |> ignore

    override this.Down() = this.Delete.Table("users") |> ignore

[<Migration(2L)>]
type CreateCommandInbox() =
    inherit Migration()

    override this.Up() =
        this.Create
            .Table("command_inbox")
            .WithColumn("id")
            .AsInt64()
            .PrimaryKey()
            .Identity()
            .WithColumn("origin")
            .AsString()
            .NotNullable()
            .WithColumn("external_key")
            .AsString()
            .Nullable()
            .Unique()
            .WithColumn("user_id")
            .AsInt64()
            .NotNullable()
            .WithColumn("chat_id")
            .AsInt64()
            .NotNullable()
            .WithColumn("payload")
            .AsString()
            .NotNullable()
            .WithColumn("priority")
            .AsInt32()
            .NotNullable()
            .WithDefaultValue(0)
            .WithColumn("status")
            .AsString()
            .NotNullable()
            .WithDefaultValue("pending")
            .WithColumn("lease_until")
            .AsInt64()
            .Nullable()
            .WithColumn("heartbeat_at")
            .AsInt64()
            .Nullable()
            .WithColumn("attempts")
            .AsInt32()
            .NotNullable()
            .WithDefaultValue(0)
            .WithColumn("max_attempts")
            .AsInt32()
            .NotNullable()
            .WithDefaultValue(5)
            .WithColumn("created_at")
            .AsInt64()
            .NotNullable()
            .WithColumn("updated_at")
            .AsInt64()
            .NotNullable()
        |> ignore

    override this.Down() =
        this.Delete.Table("command_inbox") |> ignore

[<Migration(3L)>]
type CreateMessageOutbox() =
    inherit Migration()

    override this.Up() =
        this.Create
            .Table("message_outbox")
            .WithColumn("id")
            .AsInt64()
            .PrimaryKey()
            .Identity()
            .WithColumn("command_id")
            .AsInt64()
            .NotNullable()
            .WithColumn("chunk_index")
            .AsInt32()
            .NotNullable()
            .WithColumn("chat_id")
            .AsInt64()
            .NotNullable()
            .WithColumn("random_id")
            .AsInt64()
            .NotNullable()
            .Unique()
            .WithColumn("payload")
            .AsString()
            .NotNullable()
            .WithColumn("status")
            .AsString()
            .NotNullable()
            .WithDefaultValue("pending")
            .WithColumn("remote_message_id")
            .AsInt64()
            .Nullable()
            .WithColumn("attempts")
            .AsInt32()
            .NotNullable()
            .WithDefaultValue(0)
            .WithColumn("max_attempts")
            .AsInt32()
            .NotNullable()
            .WithDefaultValue(5)
            .WithColumn("created_at")
            .AsInt64()
            .NotNullable()
            .WithColumn("updated_at")
            .AsInt64()
            .NotNullable()
        |> ignore

        // Table-level `UNIQUE(command_id, chunk_index)` — SQLite cannot add a
        // UNIQUE table constraint to an existing table, so express it as a
        // unique index, which enforces the same uniqueness.
        this.Create
            .Index("ux_message_outbox_command_chunk")
            .OnTable("message_outbox")
            .OnColumn("command_id")
            .Ascending()
            .OnColumn("chunk_index")
            .Ascending()
            .WithOptions()
            .Unique()
        |> ignore

    override this.Down() =
        this.Delete.Table("message_outbox") |> ignore

[<Migration(4L)>]
type CreateScheduleJobs() =
    inherit Migration()

    override this.Up() =
        this.Create
            .Table("schedule_jobs")
            .WithColumn("id")
            .AsInt64()
            .PrimaryKey()
            .Identity()
            .WithColumn("user_id")
            .AsInt64()
            .NotNullable()
            .WithColumn("cron_expr")
            .AsString()
            .NotNullable()
            .WithColumn("timezone")
            .AsString()
            .NotNullable()
            .WithColumn("prompt")
            .AsString()
            .NotNullable()
            .WithColumn("enabled")
            .AsInt32()
            .NotNullable()
            .WithDefaultValue(1)
            .WithColumn("catchup_policy")
            .AsString()
            .NotNullable()
            .WithDefaultValue("skip")
            .WithColumn("next_run")
            .AsInt64()
            .Nullable()
            .WithColumn("created_at")
            .AsInt64()
            .NotNullable()
            .WithColumn("updated_at")
            .AsInt64()
            .NotNullable()
        |> ignore

    override this.Down() =
        this.Delete.Table("schedule_jobs") |> ignore

[<Migration(5L)>]
type CreateScheduleRuns() =
    inherit Migration()

    override this.Up() =
        this.Create
            .Table("schedule_runs")
            .WithColumn("job_id")
            .AsInt64()
            .NotNullable()
            .WithColumn("scheduled_for")
            .AsInt64()
            .NotNullable()
            .WithColumn("status")
            .AsString()
            .NotNullable()
            .WithColumn("command_id")
            .AsInt64()
            .Nullable()
        |> ignore

        // Composite `PRIMARY KEY(job_id, scheduled_for)` — SQLite cannot add a
        // PRIMARY KEY table constraint to an existing table, so express it as a
        // unique index over the two NOT NULL columns (functionally a key).
        this.Create
            .Index("pk_schedule_runs")
            .OnTable("schedule_runs")
            .OnColumn("job_id")
            .Ascending()
            .OnColumn("scheduled_for")
            .Ascending()
            .WithOptions()
            .Unique()
        |> ignore

    override this.Down() =
        this.Delete.Table("schedule_runs") |> ignore

[<Migration(6L)>]
type CreateBackupLog() =
    inherit Migration()

    override this.Up() =
        this.Create
            .Table("backup_log")
            .WithColumn("id")
            .AsInt64()
            .PrimaryKey()
            .Identity()
            .WithColumn("started_at")
            .AsInt64()
            .NotNullable()
            .WithColumn("finished_at")
            .AsInt64()
            .Nullable()
            .WithColumn("path")
            .AsString()
            .Nullable()
            .WithColumn("checksum")
            .AsString()
            .Nullable()
            .WithColumn("status")
            .AsString()
            .NotNullable()
            .WithColumn("error")
            .AsString()
            .Nullable()
        |> ignore

    override this.Down() =
        this.Delete.Table("backup_log") |> ignore

[<Migration(7L)>]
type AddOutboxEntities() =
    inherit Migration()

    override this.Up() =
        // Entities for outbound messages are stored as a JSON text column so the
        // storage layer never depends on the Telegram entity type.
        this.Alter.Table("message_outbox").AddColumn("entities").AsString().Nullable()
        |> ignore

    override this.Down() =
        this.Delete.Column("entities").FromTable("message_outbox") |> ignore

[<Migration(8L)>]
type AddCommandInboxImages() =
    inherit Migration()

    override this.Up() =
        // Images (base64) accompanying a command are stored as a JSON text
        // column; an empty list is stored as an empty string.
        this.Alter.Table("command_inbox").AddColumn("images").AsString().Nullable()
        |> ignore

    override this.Down() =
        this.Delete.Column("images").FromTable("command_inbox") |> ignore

[<Migration(9L)>]
type ExtendScheduleJobs() =
    inherit Migration()

    override this.Up() =
        // The `enabled` flag is absorbed by the richer `status` lifecycle
        // (pending|active|paused|cancelled|expired). New columns carry the
        // delivery chat, the interval sugar (exactly one of cron/interval), the
        // restart-safe idempotency key for schedule_add, and last-run/last-error
        // diagnostics.
        this.Alter.Table("schedule_jobs").AddColumn("chat_id").AsInt64().NotNullable().WithDefaultValue(0L)
        |> ignore

        this.Alter.Table("schedule_jobs").AddColumn("interval_seconds").AsInt32().Nullable()
        |> ignore

        this.Alter.Table("schedule_jobs").AddColumn("status").AsString().NotNullable().WithDefaultValue("pending")
        |> ignore

        this.Alter.Table("schedule_jobs").AddColumn("origin_tool_call_id").AsString().Nullable()
        |> ignore

        this.Alter.Table("schedule_jobs").AddColumn("last_run_at").AsInt64().Nullable()
        |> ignore

        this.Alter.Table("schedule_jobs").AddColumn("last_error").AsString().Nullable()
        |> ignore

        // Restart-safe idempotency for schedule_add. SQLite allows multiple NULLs
        // in a unique index, so the (mostly NULL) tool-call-id column stays unique
        // per non-NULL value.
        this.Create
            .Index("ux_schedule_jobs_origin_tool_call_id")
            .OnTable("schedule_jobs")
            .OnColumn("origin_tool_call_id")
            .Ascending()
            .WithOptions()
            .Unique()
        |> ignore

        this.Delete.Column("enabled").FromTable("schedule_jobs") |> ignore

    override this.Down() =
        this.Alter.Table("schedule_jobs").AddColumn("enabled").AsInt32().NotNullable().WithDefaultValue(1)
        |> ignore

        this.Delete.Index("ux_schedule_jobs_origin_tool_call_id").OnTable("schedule_jobs")
        |> ignore

        this.Delete.Column("chat_id").FromTable("schedule_jobs") |> ignore
        this.Delete.Column("interval_seconds").FromTable("schedule_jobs") |> ignore
        this.Delete.Column("status").FromTable("schedule_jobs") |> ignore
        this.Delete.Column("origin_tool_call_id").FromTable("schedule_jobs") |> ignore
        this.Delete.Column("last_run_at").FromTable("schedule_jobs") |> ignore
        this.Delete.Column("last_error").FromTable("schedule_jobs") |> ignore

[<Migration(10L)>]
type AddScheduleAfterSeconds() =
    inherit Migration()

    override this.Up() =
        // One-shot schedule: `after_seconds` fires a job exactly once that many
        // seconds after confirmation, then the job auto-completes.
        this.Alter.Table("schedule_jobs").AddColumn("after_seconds").AsInt32().Nullable()
        |> ignore

    override this.Down() =
        this.Delete.Column("after_seconds").FromTable("schedule_jobs") |> ignore

[<Migration(11L)>]
type MakeScheduleCronNullable() =
    inherit Migration()

    // SQLite cannot change a column's nullability via ALTER, so both Up and Down
    // rebuild the table with the desired `cron_expr` constraint and copy the data.
    member private this.Rebuild(cronNotNullable: bool, tempTable: string) =
        let cronExpr = if cronNotNullable then "TEXT NOT NULL" else "TEXT"

        this.Execute.Sql(
            sprintf
                """CREATE TABLE %s (
                        id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        user_id INTEGER NOT NULL,
                        cron_expr %s,
                        timezone TEXT NOT NULL,
                        prompt TEXT NOT NULL,
                        catchup_policy TEXT NOT NULL DEFAULT 'skip',
                        next_run INTEGER,
                        created_at INTEGER NOT NULL,
                        updated_at INTEGER NOT NULL,
                        chat_id INTEGER NOT NULL DEFAULT 0,
                        interval_seconds INTEGER,
                        status TEXT NOT NULL DEFAULT 'pending',
                        origin_tool_call_id TEXT,
                        last_run_at INTEGER,
                        last_error TEXT,
                        after_seconds INTEGER
                    )"""
                tempTable
                cronExpr
        )
        |> ignore

        this.Execute.Sql(
            sprintf
                """INSERT INTO %s
                        (id, user_id, cron_expr, timezone, prompt, catchup_policy, next_run,
                         created_at, updated_at, chat_id, interval_seconds, status,
                         origin_tool_call_id, last_run_at, last_error, after_seconds)
                    SELECT id, user_id, cron_expr, timezone, prompt, catchup_policy, next_run,
                         created_at, updated_at, chat_id, interval_seconds, status,
                         origin_tool_call_id, last_run_at, last_error, after_seconds
                    FROM schedule_jobs"""
                tempTable
        )
        |> ignore

        this.Execute.Sql("DROP TABLE schedule_jobs") |> ignore

        this.Execute.Sql(sprintf "ALTER TABLE %s RENAME TO schedule_jobs" tempTable)
        |> ignore

        this.Execute.Sql(
            "CREATE UNIQUE INDEX ux_schedule_jobs_origin_tool_call_id ON schedule_jobs (origin_tool_call_id ASC)"
        )
        |> ignore

    override this.Up() =
        // One-shot (after_seconds) and interval jobs carry no cron expression;
        // the historical migration 4 shipped cron_expr NOT NULL, so existing
        // databases still enforce it — rebuild the table with a nullable column.
        this.Rebuild(false, "schedule_jobs_new")

    override this.Down() = this.Rebuild(true, "schedule_jobs_old")

[<Migration(12L)>]
type AddScheduleRunAt() =
    inherit Migration()

    override this.Up() =
        // Absolute one-time calendar occurrence, stored as UTC Unix seconds.
        this.Alter.Table("schedule_jobs").AddColumn("run_at").AsInt64().Nullable()
        |> ignore

    override this.Down() =
        this.Delete.Column("run_at").FromTable("schedule_jobs") |> ignore

// ---------------------------------------------------------------------------
// Runner
// ---------------------------------------------------------------------------

/// Builds the connection string used by the FluentMigrator runner. The runner
/// opens its own connections, so it must apply the same `foreign_keys` and
/// `DefaultTimeout` settings the storage executor applies on every connection
/// (see `StorageInternals`); otherwise FK semantics would differ between the
/// executor and the migration runner.
let private runnerConnectionString (options: StorageOptions) : string =
    let builder = SqliteConnectionStringBuilder()
    builder.DataSource <- options.DatabasePath
    builder.Mode <- SqliteOpenMode.ReadWriteCreate
    builder.ForeignKeys <- true
    builder.DefaultTimeout <- max 1 (int options.BusyTimeout.TotalSeconds)
    builder.ConnectionString

let private withRunner (options: StorageOptions) (f: IMigrationRunner -> unit) : unit =
    let services =
        ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(fun rb ->
                rb
                    .AddSQLite()
                    .WithGlobalConnectionString(runnerConnectionString options)
                    .ScanIn(typeof<CreateUsers>.Assembly)
                    .For.Migrations()
                |> ignore)
            .AddLogging(fun lb -> lb.AddFluentMigratorConsole() |> ignore)

    use provider = services.BuildServiceProvider()
    let runner = provider.GetRequiredService<IMigrationRunner>()
    f runner

/// Applies every pending FluentMigrator migration to the database. Idempotent:
/// migrations already applied (tracked in `VersionInfo`) are skipped.
let run (options: StorageOptions) : unit =
    withRunner options (fun runner -> runner.MigrateUp())

/// Migrates up to `version` (inclusive): applies all pending migrations with a
/// version <= `version`.
let migrateUp (options: StorageOptions) (version: int64) : unit =
    withRunner options (fun runner -> runner.MigrateUp(version))

/// Migrates down to `version` (exclusive): rolls back every applied migration
/// with a version > `version`, executing each migration's `Down()` in reverse
/// order. `MigrateDown 0L` rolls back the entire schema.
let migrateDown (options: StorageOptions) (version: int64) : unit =
    withRunner options (fun runner -> runner.MigrateDown(version))
