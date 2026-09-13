namespace Phos.Storage

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite

/// Configuration for a SQLite-backed storage executor.
type StorageOptions =
    { DatabasePath: string
      BusyTimeout: TimeSpan
      ReadPoolSize: int
      CheckpointEvery: int }

/// Private helpers that open and configure SQLite connections.
module private StorageInternals =

    let connectionString (options: StorageOptions) : string =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- options.DatabasePath
        builder.Mode <- SqliteOpenMode.ReadWriteCreate
        // Microsoft.Data.Sqlite drives the SQLite busy handler (and command
        // timeout) from `DefaultTimeout`; keep it in lock-step with `BusyTimeout`
        // so a locked database waits a bounded time instead of the 30s default.
        builder.DefaultTimeout <- max 1 (int options.BusyTimeout.TotalSeconds)
        builder.ConnectionString

    let configure (conn: SqliteConnection) (busyTimeoutMs: int) : unit =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sprintf "PRAGMA busy_timeout = %d;" busyTimeoutMs
        cmd.ExecuteNonQuery() |> ignore
        use cmd2 = conn.CreateCommand()
        cmd2.CommandText <- "PRAGMA foreign_keys = ON;"
        cmd2.ExecuteNonQuery() |> ignore

    let openConnection (options: StorageOptions) : SqliteConnection =
        let conn = new SqliteConnection(connectionString options)
        conn.Open()
        configure conn (int options.BusyTimeout.TotalMilliseconds)
        conn

/// A single-writer, bounded-reader SQLite executor.
///
/// All writes are serialized through a `SemaphoreSlim(1)` and every write opens
/// its own connection, so an uncommitted transaction is rolled back when the
/// connection is disposed. Reads are bounded by `ReadPoolSize` and likewise open
/// a fresh connection per operation, so no connection is ever shared across
/// threads. WAL, `busy_timeout` and `foreign_keys` are enabled on every
/// connection; the WAL journal is checkpointed (`wal_checkpoint(TRUNCATE)`)
/// after every `CheckpointEvery` writes and on demand via `CheckpointNow`.
///
/// Microsoft.Data.Sqlite has no true async I/O (it executes synchronously), so
/// the executor performs each operation synchronously under the relevant
/// semaphore and returns an already-completed `Task`; this keeps the operation
/// bounded and serialized without an intermediate async state machine.
type StorageExecutor private (options: StorageOptions) =
    let writeLock = new SemaphoreSlim(1, 1)

    let readLock =
        new SemaphoreSlim(max options.ReadPoolSize 1, max options.ReadPoolSize 1)

    let mutable writeCount = 0
    let mutable disposed = false

    let checkpoint () =
        use conn = StorageInternals.openConnection options
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA wal_checkpoint(TRUNCATE);"
        cmd.ExecuteNonQuery() |> ignore

    let ensureNotDisposed () =
        if disposed then
            invalidOp "StorageExecutor is disposed"

    /// Runs `f` with a dedicated write connection, serialized as the sole writer.
    member this.WriteAsync(f: SqliteConnection -> 'T) : Task<'T> =
        ensureNotDisposed ()
        writeLock.Wait()

        try
            use conn = StorageInternals.openConnection options
            let result = f conn

            writeCount <- writeCount + 1

            if options.CheckpointEvery > 0 && writeCount >= options.CheckpointEvery then
                checkpoint ()
                writeCount <- 0

            Task.FromResult result
        finally
            writeLock.Release() |> ignore

    /// Runs `f` with a dedicated read connection, bounded by `ReadPoolSize`.
    member this.ReadAsync(f: SqliteConnection -> 'T) : Task<'T> =
        ensureNotDisposed ()
        readLock.Wait()

        try
            use conn = StorageInternals.openConnection options
            Task.FromResult(f conn)
        finally
            readLock.Release() |> ignore

    /// Forces a WAL checkpoint now.
    member this.CheckpointNow() : Task =
        ensureNotDisposed ()
        writeLock.Wait()

        try
            checkpoint ()
            writeCount <- 0
            Task.CompletedTask
        finally
            writeLock.Release() |> ignore

    /// Releases the semaphores. Idempotent; safe to call more than once.
    member this.Dispose() =
        if not disposed then
            disposed <- true
            writeLock.Dispose()
            readLock.Dispose()

    interface IDisposable with
        member this.Dispose() = this.Dispose()

    /// Opens (creating if necessary) the database and establishes WAL mode.
    static member Create(options: StorageOptions) : StorageExecutor =
        use conn = StorageInternals.openConnection options
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "PRAGMA journal_mode = WAL;"
        cmd.ExecuteScalar() |> ignore
        new StorageExecutor(options)

[<AutoOpen>]
module StorageOpen =
    /// Opens (creating if necessary) the database at `options.DatabasePath` and
    /// returns a ready-to-use `StorageExecutor`.
    let openExecutor (options: StorageOptions) : StorageExecutor = StorageExecutor.Create options
