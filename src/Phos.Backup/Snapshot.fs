namespace Phos.Backup

open System
open System.IO
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Microsoft.Data.Sqlite

/// Builds the staging directory for a backup: consistent SQLite snapshots via
/// `VACUUM INTO`, stable file copies, and volatile-copy-with-retry for live
/// JSONL session logs.
module Snapshot =

    /// `Path.GetFileName` is nullable-annotated; unwrap to a non-null string.
    let private fileName (path: string) : string =
        match Path.GetFileName path with
        | null -> ""
        | s -> s

    /// Creates the parent directory of `dest` if it has one.
    let private ensureParentDir (dest: string) : unit =
        match Path.GetDirectoryName dest with
        | null
        | "" -> ()
        | dir -> Directory.CreateDirectory dir |> ignore

    /// Snapshots a SQLite database by `VACUUM INTO` to `dest` on a fresh
    /// connection (so the snapshot is a consistent, compact copy that includes
    /// any committed WAL data). The destination must not already exist; parent
    /// directories are created. Single quotes in `dest` are escaped. Any
    /// `SqliteException` (including a missing source file) becomes an `Error`.
    let snapshotSqlite (source: string) (dest: string) : Result<unit, string> =
        if not (File.Exists source) then
            Error(sprintf "source database does not exist: %s" source)
        elif File.Exists dest then
            Error(sprintf "destination already exists: %s" dest)
        else
            ensureParentDir dest

            try
                let csb = SqliteConnectionStringBuilder()
                csb.DataSource <- source
                csb.Mode <- SqliteOpenMode.ReadWrite

                use conn = new SqliteConnection(csb.ConnectionString)
                conn.Open()

                use cmd = conn.CreateCommand()
                cmd.CommandText <- "PRAGMA busy_timeout = 2000;"
                cmd.ExecuteNonQuery() |> ignore

                let escaped = dest.Replace("'", "''")

                use vacuum = conn.CreateCommand()
                vacuum.CommandText <- sprintf "VACUUM INTO '%s';" escaped
                vacuum.ExecuteNonQuery() |> ignore

                Ok()
            with ex ->
                Error ex.Message

    /// Copies a file believed to be static. Creates parent directories.
    let copyStable (source: string) (dest: string) : Result<unit, string> =
        try
            if not (File.Exists source) then
                Error(sprintf "source file does not exist: %s" source)
            else
                ensureParentDir dest
                File.Copy(source, dest, true)
                Ok()
        with ex ->
            Error ex.Message

    /// Copies a file that may be appended to concurrently (JSONL session logs).
    /// Copies, then compares `Length` and `LastWriteTimeUtc` before/after; equal
    /// means the source did not change during the copy. On change it retries up
    /// to `maxAttempts`; if the file keeps changing it returns an `Error`
    /// "file keeps changing: <src>" rather than shipping a torn archive.
    let copyVolatile (source: string) (dest: string) (maxAttempts: int) : Task<Result<unit, string>> =
        task {
            try
                if not (File.Exists source) then
                    return Error(sprintf "source file does not exist: %s" source)
                else
                    ensureParentDir dest

                    let mutable attempt = 0
                    let mutable success = false

                    while attempt < maxAttempts && not success do
                        let before =
                            let fi = FileInfo source
                            (fi.Length, fi.LastWriteTimeUtc)

                        File.Copy(source, dest, true)

                        let after =
                            let fi = FileInfo source
                            (fi.Length, fi.LastWriteTimeUtc)

                        if before = after then
                            success <- true
                        else
                            attempt <- attempt + 1

                            if attempt < maxAttempts then
                                do! Task.Delay 50

                    if success then
                        return Ok()
                    else
                        return Error(sprintf "file keeps changing: %s" source)
            with ex ->
                return Error ex.Message
        }

    /// Recursively collects files under `dir`, excluding `.env`, `*.db-wal`,
    /// `*.db-shm`, and any directory whose name starts with `-tmp-`.
    let rec private collectFiles (dir: string) : string list =
        let files =
            Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
            |> Seq.filter (fun f ->
                let name = fileName f
                name <> ".env" && not (name.EndsWith ".db-wal") && not (name.EndsWith ".db-shm"))
            |> Seq.sort
            |> Seq.toList

        let subDirs =
            Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly)
            |> Seq.filter (fun d -> not (fileName(d).StartsWith "-tmp-"))
            |> Seq.sort
            |> Seq.toList

        files @ (subDirs |> List.collect collectFiles)

    /// Builds the staging directory described by `options`. Creates a fresh
    /// staging tree and copies/snapshots every data file, returning a list of
    /// `(relPath, size, sha256)` for every file that will be listed in the
    /// manifest. `manifest.json` itself is NOT included. Rel paths use forward
    /// slashes.
    let buildStaging
        (options: BackupOptions)
        (stagingDir: string)
        (schemaVersion: int64)
        : Task<Result<(string * int64 * string) list, string>> =
        taskResult {
            if Directory.Exists stagingDir then
                Directory.Delete(stagingDir, true)

            Directory.CreateDirectory stagingDir |> ignore

            let entries = ResizeArray<string * int64 * string>()

            let addFile (relPath: string) (destPath: string) : Result<unit, string> =
                result {
                    let! sha = Manifest.sha256File destPath
                    let info = FileInfo destPath
                    entries.Add(relPath, info.Length, sha)
                    return ()
                }

            // Host database (required).
            do!
                Result.requireTrue
                    (sprintf "host database missing: %s" options.DatabasePath)
                    (File.Exists options.DatabasePath)

            let hostDbDest = Path.Combine(stagingDir, "phos.db")
            do! snapshotSqlite options.DatabasePath hostDbDest
            do! addFile "phos.db" hostDbDest

            // OMP agent profile (required directory).
            let agentDir = options.ProfileDir
            do! Result.requireTrue (sprintf "profile agent directory missing: %s" agentDir) (Directory.Exists agentDir)

            let agentDbSrc = Path.Combine(agentDir, "agent.db")
            do! Result.requireTrue (sprintf "agent.db missing: %s" agentDbSrc) (File.Exists agentDbSrc)

            let agentDbDest = Path.Combine(stagingDir, "omp", "agent.db")
            do! snapshotSqlite agentDbSrc agentDbDest
            do! addFile "omp/agent.db" agentDbDest

            let modelsDbSrc = Path.Combine(agentDir, "models.db")
            do! Result.requireTrue (sprintf "models.db missing: %s" modelsDbSrc) (File.Exists modelsDbSrc)

            let modelsDbDest = Path.Combine(stagingDir, "omp", "models.db")
            do! snapshotSqlite modelsDbSrc modelsDbDest
            do! addFile "omp/models.db" modelsDbDest

            let configSrc = Path.Combine(agentDir, "config.yml")
            do! Result.requireTrue (sprintf "config.yml missing: %s" configSrc) (File.Exists configSrc)

            let configDest = Path.Combine(stagingDir, "omp", "config.yml")
            do! copyStable configSrc configDest
            do! addFile "omp/config.yml" configDest

            let modelsYmlSrc = Path.Combine(agentDir, "models.yml")
            do! Result.requireTrue (sprintf "models.yml missing: %s" modelsYmlSrc) (File.Exists modelsYmlSrc)

            let modelsYmlDest = Path.Combine(stagingDir, "omp", "models.yml")
            do! copyStable modelsYmlSrc modelsYmlDest
            do! addFile "omp/models.yml" modelsYmlDest

            // Content-addressed blobs (optional directory; immutable names).
            let blobsDir = Path.Combine(agentDir, "blobs")

            if Directory.Exists blobsDir then
                for blob in
                    Directory.EnumerateFiles(blobsDir, "*", SearchOption.TopDirectoryOnly)
                    |> Seq.sort do
                    let name = fileName blob
                    let dest = Path.Combine(stagingDir, "omp", "blobs", name)
                    do! copyStable blob dest
                    do! addFile (sprintf "omp/blobs/%s" name) dest

            // Mnemopi memory banks (optional directory).
            let banksDir = Path.Combine(agentDir, "memories", "mnemopi", "banks")

            if Directory.Exists banksDir then
                for bank in
                    Directory.EnumerateDirectories(banksDir, "*", SearchOption.TopDirectoryOnly)
                    |> Seq.sort do
                    let bankName = fileName bank

                    if not (bankName.StartsWith "-tmp-") then
                        let mnemopiSrc = Path.Combine(bank, "mnemopi.db")

                        if File.Exists mnemopiSrc then
                            let dest =
                                Path.Combine(stagingDir, "omp", "memories", "mnemopi", "banks", bankName, "mnemopi.db")

                            do! snapshotSqlite mnemopiSrc dest
                            do! addFile (sprintf "omp/memories/mnemopi/banks/%s/mnemopi.db" bankName) dest

            // Sessions (optional directory; live-appended JSONL).
            let sessionsDir = Path.Combine(agentDir, "sessions")

            if Directory.Exists sessionsDir then
                for ws in
                    Directory.EnumerateDirectories(sessionsDir, "*", SearchOption.TopDirectoryOnly)
                    |> Seq.sort do
                    let wsName = fileName ws

                    if not (wsName.StartsWith "-tmp-") then
                        for sess in
                            Directory.EnumerateFiles(ws, "*.jsonl", SearchOption.TopDirectoryOnly)
                            |> Seq.sort do
                            let sessName = fileName sess
                            let dest = Path.Combine(stagingDir, "omp", "sessions", wsName, sessName)
                            do! copyVolatile sess dest 3
                            do! addFile (sprintf "omp/sessions/%s/%s" wsName sessName) dest

            // Per-user workspaces (optional root; recursive copy).
            if Directory.Exists options.WorkspaceRoot then
                for uidDir in
                    Directory.EnumerateDirectories(options.WorkspaceRoot, "*", SearchOption.TopDirectoryOnly)
                    |> Seq.sort do
                    let uidName = fileName uidDir

                    if not (uidName.StartsWith "-tmp-") then
                        for file in collectFiles uidDir do
                            let rel =
                                Path.GetRelativePath(uidDir, file).Replace(Path.DirectorySeparatorChar, '/')

                            let dest = Path.Combine(stagingDir, "workspaces", uidName, rel)
                            do! copyStable file dest
                            do! addFile (sprintf "workspaces/%s/%s" uidName rel) dest

            return List.ofSeq entries
        }
