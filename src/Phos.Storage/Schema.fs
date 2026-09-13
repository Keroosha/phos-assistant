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
