module Phos.IntegrationTests.BackupIntegrationTests

// FS3511 (state machine not statically compilable) is a performance-only
// warning emitted by the F# `task` builder when a nested `task {}` is invoked
// through a higher-order helper. It has no correctness impact and is expected
// for this test harness (see IntegrationTests.fs).
#nowarn "3511"

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Formats.Tar
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open Microsoft.Extensions.Logging.Abstractions
open Microsoft.Data.Sqlite
open Phos.Storage
open Phos.Backup

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private tempDir () : string =
    let dir =
        Path.Combine(Path.GetTempPath(), "phos-bkp-it-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(dir) |> ignore
    dir

let private deleteDir (dir: string) =
    try
        if Directory.Exists dir then
            Directory.Delete(dir, true)
    with _ ->
        ()

let private ensureParentDir (path: string) : unit =
    match Path.GetDirectoryName path with
    | null
    | "" -> ()
    | d -> Directory.CreateDirectory d |> ignore

let private expectOk (r: Result<'T, string>) : 'T =
    match r with
    | Ok v -> v
    | Error e -> failwithf "Expected Ok, got Error: %s" e

let private defaultOptions (dbPath: string) : StorageOptions =
    { DatabasePath = dbPath
      BusyTimeout = TimeSpan.FromSeconds 2.0
      ReadPoolSize = 4
      CheckpointEvery = 10 }

/// Resolves the `age` binary from `PHOS_AGE_PATH` or `PATH`. Fails (rather than
/// silently skipping) when age is not installed, per the CI recipe.
let private findAge () : string =
    let envPath = Environment.GetEnvironmentVariable "PHOS_AGE_PATH"

    match Option.ofObj envPath with
    | Some p when p <> "" -> p
    | _ ->
        let path = Environment.GetEnvironmentVariable "PATH"

        let dirs =
            match Option.ofObj path with
            | Some p -> p.Split(Path.PathSeparator)
            | None -> [||]

        match dirs |> Array.map (fun d -> Path.Combine(d, "age")) |> Array.tryFind File.Exists with
        | Some p -> p
        | None -> failwith "age not found — install age (https://github.com/FiloSottile/age)"

/// Resolves `age-keygen` alongside `age` (they ship together), falling back to
/// `PATH`.
let private findAgeKeygen () : string =
    let dir =
        match Path.GetDirectoryName(findAge ()) with
        | null -> ""
        | d -> d

    let candidate = Path.Combine(dir, "age-keygen")

    if File.Exists candidate then
        candidate
    else
        let path = Environment.GetEnvironmentVariable "PATH"

        let dirs =
            match Option.ofObj path with
            | Some p -> p.Split(Path.PathSeparator)
            | None -> [||]

        match
            dirs
            |> Array.map (fun d -> Path.Combine(d, "age-keygen"))
            |> Array.tryFind File.Exists
        with
        | Some p -> p
        | None -> failwith "age-keygen not found — install age (https://github.com/FiloSottile/age)"

type private ProcResult =
    { ExitCode: int
      StdOut: string
      StdErr: string }

/// Runs an external binary with an argument list (no shell), capturing stdout and
/// stderr. Returns the exit code and both streams.
let private runProc (exe: string) (args: string list) : Task<ProcResult> =
    task {
        let psi = ProcessStartInfo(exe)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        for a in args do
            psi.ArgumentList.Add a

        use p = new Process()
        p.StartInfo <- psi

        if not (p.Start()) then
            failwithf "failed to start %s" exe

        let outTask = p.StandardOutput.ReadToEndAsync()
        let errTask = p.StandardError.ReadToEndAsync()
        do! p.WaitForExitAsync()
        let! out = outTask
        let! err = errTask

        return
            { ExitCode = p.ExitCode
              StdOut = out
              StdErr = err }
    }

let private createSqlite (dbPath: string) (ddl: string) : unit =
    ensureParentDir dbPath

    use conn = new SqliteConnection(sprintf "Data Source=%s" dbPath)
    conn.Open()

    use cmd = conn.CreateCommand()
    cmd.CommandText <- ddl
    cmd.ExecuteNonQuery() |> ignore

/// Extracts a `.tar.gz` into `destDir`, preserving the relative entry names.
let private extractTarGz (tarPath: string) (destDir: string) : unit =
    use fs = File.OpenRead tarPath
    use gz = new GZipStream(fs, CompressionMode.Decompress)
    use tr = new TarReader(gz)
    let mutable entry = Option.ofObj (tr.GetNextEntry())

    while entry.IsSome do
        let e = entry.Value

        if e.EntryType = TarEntryType.RegularFile then
            let rel = e.Name.TrimStart('/')
            let outPath = Path.Combine(destDir, rel)
            ensureParentDir outPath

            use outFs = File.Create outPath

            match e.DataStream with
            | null -> ()
            | s -> s.CopyTo outFs

        entry <- Option.ofObj (tr.GetNextEntry())

/// Context returned by `createDrill` for the restore/corrupt assertions.
type private Drill =
    { Temp: string
      Identity: string
      Recipient: string
      Archive: string
      RestoreDir: string
      LogRepo: IBackupLogRepository
      Cleanup: unit -> unit }

/// Builds a disposable environment — host db, fake OMP profile, workspace, age
/// keys — and runs one backup, returning the produced archive plus a handle to
/// the backup_log repository.
let private createDrill () : Task<Drill> =
    task {
        let temp = tempDir ()
        let hostDb = Path.Combine(temp, "host.db")
        let profileDir = Path.Combine(temp, "agent")
        let workspaceRoot = Path.Combine(temp, "workspaces")
        let backups = Path.Combine(temp, "backups")
        let logDb = Path.Combine(temp, "log", "backup.db")
        ensureParentDir logDb
        let logExec = StorageExecutor.Create(defaultOptions logDb)
        let mutable keep = false

        try
            // Host db: migrated schema (VersionInfo = 11) + a users row.
            Schema.migrateUp (defaultOptions hostDb) 11L

            use conn = new SqliteConnection(sprintf "Data Source=%s" hostDb)
            conn.Open()

            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                "INSERT INTO users (user_id, username, role, workspace_path, timezone, created_at, updated_at) VALUES (1,'alice','admin','/tmp','UTC',0,0);"

            cmd.ExecuteNonQuery() |> ignore

            // Fake OMP profile (agent dir): required config/models, agent.db,
            // models.db, a mnemopi bank, sessions, and a blob.
            let bankDir = Path.Combine(profileDir, "memories", "mnemopi", "banks", "bank1")
            let sessionDir = Path.Combine(profileDir, "sessions", "ws1")

            Directory.CreateDirectory(Path.Combine(profileDir, "blobs")) |> ignore
            Directory.CreateDirectory bankDir |> ignore
            Directory.CreateDirectory sessionDir |> ignore

            File.WriteAllText(Path.Combine(profileDir, "config.yml"), "api: key\n")
            File.WriteAllText(Path.Combine(profileDir, "models.yml"), "modelRoles:\n  - a\n")
            File.WriteAllBytes(Path.Combine(profileDir, "blobs", "1.bin"), [| 1uy; 2uy; 3uy |])
            createSqlite (Path.Combine(profileDir, "agent.db")) "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);"
            createSqlite (Path.Combine(profileDir, "models.db")) "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);"
            createSqlite (Path.Combine(bankDir, "mnemopi.db")) "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);"
            File.WriteAllText(Path.Combine(sessionDir, "s.jsonl"), "{\"a\":1}\n{\"b\":2}\n")

            // Workspace with an .omp/APPEND_SYSTEM.md file.
            let wsOmp = Path.Combine(workspaceRoot, "u1", ".omp")
            Directory.CreateDirectory wsOmp |> ignore
            File.WriteAllText(Path.Combine(wsOmp, "APPEND_SYSTEM.md"), "# system\n")

            // age keys: identity + recipient (public key).
            let identity = Path.Combine(temp, "identity")
            let recipient = Path.Combine(temp, "recipient.pub")
            let! k1 = runProc (findAgeKeygen ()) [ "-o"; identity ]
            k1.ExitCode |> should equal 0
            let! k2 = runProc (findAgeKeygen ()) [ "-y"; "-o"; recipient; identity ]
            k2.ExitCode |> should equal 0

            // backup_log repository on a separate migrated db.
            Schema.run (defaultOptions logDb)
            let logRepo = BackupLogRepository(logExec) :> IBackupLogRepository

            let options: BackupOptions =
                { Directory = backups
                  Interval = TimeSpan.FromHours 1.0
                  AgeRecipient = recipient
                  RetainCount = 3
                  DatabasePath = hostDb
                  ProfileDir = profileDir
                  WorkspaceRoot = workspaceRoot
                  OmpPath = Path.Combine(temp, "omp") }

            let svc: IBackupService = BackupService(options, logRepo, NullLogger.Instance)
            let! r = svc.RunNow()
            r |> expectOk |> ignore

            let archives = Directory.GetFiles(backups, "phos-backup-*.age")
            archives |> should haveLength 1

            let cleanup () =
                logExec.Dispose()
                deleteDir temp

            keep <- true

            return
                { Temp = temp
                  Identity = identity
                  Recipient = recipient
                  Archive = archives.[0]
                  RestoreDir = Path.Combine(temp, "restore")
                  LogRepo = logRepo
                  Cleanup = cleanup }
        finally
            if not keep then
                logExec.Dispose()
                deleteDir temp
    }

// ---------------------------------------------------------------------------
// Drill: backup produces an archive and an "ok" log entry
// ---------------------------------------------------------------------------

[<Fact>]
let ``backup service writes archive and ok log entry`` () =
    task {
        let! d = createDrill ()

        try
            File.Exists d.Archive |> should be True

            let! entries = d.LogRepo.ListRecent 1
            entries |> should haveLength 1
            let e: BackupLogEntry = entries.[0]
            e.Status |> should equal "ok"
            e.Path |> should not' (equal None)

            match e.Checksum with
            | Some c -> c |> should not' (equal "")
            | None -> failwith "expected a non-empty checksum"
        finally
            d.Cleanup()
    }

// ---------------------------------------------------------------------------
// Drill: disposable restore recovers a verified, intact archive
// ---------------------------------------------------------------------------

[<Fact>]
let ``restore drill recovers data`` () =
    task {
        let! d = createDrill ()

        try
            let restoreDir = d.RestoreDir
            Directory.CreateDirectory restoreDir |> ignore
            let tarGz = Path.Combine(restoreDir, "restore.tar.gz")
            let! dec = runProc (findAge ()) [ "-d"; "-i"; d.Identity; "-o"; tarGz; d.Archive ]
            dec.ExitCode |> should equal 0
            extractTarGz tarGz restoreDir

            // Manifest roundtrips and every file checksum/size verifies.
            let manifestJson = File.ReadAllText(Path.Combine(restoreDir, "manifest.json"))
            let manifest = Manifest.ofJson manifestJson |> expectOk
            Manifest.verify restoreDir manifest |> expectOk |> ignore

            // Each sqlite snapshot is intact.
            for rel in
                [ "phos.db"
                  "omp/agent.db"
                  "omp/models.db"
                  "omp/memories/mnemopi/banks/bank1/mnemopi.db" ] do
                let dbPath = Path.Combine(restoreDir, rel)
                File.Exists dbPath |> should be True

                use conn = new SqliteConnection(sprintf "Data Source=%s" dbPath)
                conn.Open()

                use cmd = conn.CreateCommand()
                cmd.CommandText <- "PRAGMA integrity_check;"

                match cmd.ExecuteScalar() with
                | :? string as s -> s |> should equal "ok"
                | _ -> failwith "integrity_check returned non-string"

            // Host schema version preserved.
            use conn =
                new SqliteConnection(sprintf "Data Source=%s" (Path.Combine(restoreDir, "phos.db")))

            conn.Open()

            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT MAX(Version) FROM VersionInfo;"

            match cmd.ExecuteScalar() with
            | :? int64 as v -> v |> should equal 11L
            | _ -> failwith "VersionInfo returned non-int64"

            // OMP profile: models.yml keeps modelRoles, the bank db is present,
            // and every session line is valid JSON.
            let modelsYml = File.ReadAllText(Path.Combine(restoreDir, "omp", "models.yml"))
            modelsYml.Contains "modelRoles" |> should be True

            File.Exists(Path.Combine(restoreDir, "omp", "memories", "mnemopi", "banks", "bank1", "mnemopi.db"))
            |> should be True

            let sessionLines =
                File.ReadAllLines(Path.Combine(restoreDir, "omp", "sessions", "ws1", "s.jsonl"))

            sessionLines |> should haveLength 2

            for line in sessionLines do
                use _ = JsonDocument.Parse line
                ()
        finally
            d.Cleanup()
    }

// ---------------------------------------------------------------------------
// Drill: a corrupted archive is rejected
// ---------------------------------------------------------------------------

[<Fact>]
let ``corrupt archive is rejected`` () =
    task {
        let! d = createDrill ()

        try
            let corrupt = Path.Combine(d.Temp, "corrupt.age")
            let bytes = File.ReadAllBytes d.Archive
            let idx = bytes.Length / 2
            bytes.[idx] <- bytes.[idx] ^^^ 0xFFuy
            File.WriteAllBytes(corrupt, bytes)

            let out = Path.Combine(d.Temp, "corrupt.tar.gz")
            let! dec = runProc (findAge ()) [ "-d"; "-i"; d.Identity; "-o"; out; corrupt ]

            if dec.ExitCode <> 0 then
                // age rejected the corrupted ciphertext — expected rejection.
                ()
            else
                // age accepted it (unlikely); the manifest verify must reject.
                let restoreDir = Path.Combine(d.Temp, "corrupt-restore")
                Directory.CreateDirectory restoreDir |> ignore
                extractTarGz out restoreDir
                let manifestJson = File.ReadAllText(Path.Combine(restoreDir, "manifest.json"))
                let manifest = Manifest.ofJson manifestJson |> expectOk

                match Manifest.verify restoreDir manifest with
                | Ok() -> failwith "expected corrupt archive to be rejected"
                | Error _ -> ()
        finally
            d.Cleanup()
    }

// ---------------------------------------------------------------------------
// Drill: encryptAge fails loudly on a missing input file
// ---------------------------------------------------------------------------

[<Fact>]
let ``encrypt age fails on missing input file`` () =
    task {
        let dir = tempDir ()

        try
            let identity = Path.Combine(dir, "identity")
            let recipient = Path.Combine(dir, "recipient.pub")
            let! k1 = runProc (findAgeKeygen ()) [ "-o"; identity ]
            k1.ExitCode |> should equal 0
            let! k2 = runProc (findAgeKeygen ()) [ "-y"; "-o"; recipient; identity ]
            k2.ExitCode |> should equal 0

            let out = Path.Combine(dir, "out.age")

            match Archive.encryptAge (Path.Combine(dir, "missing.tar.gz")) out recipient with
            | Ok() -> failwith "expected encrypt to fail"
            | Error e -> e.Contains "age" |> should be True
        finally
            deleteDir dir
    }
