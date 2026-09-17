module Phos.Tests.BackupTests

// FS3511 (state machine not statically compilable) is a performance-only
// warning emitted by the F# `task` builder when a nested `task {}` is invoked
// through a higher-order helper. It has no correctness impact and is expected
// for this test harness (see IntegrationTests.fs).
#nowarn "3511"

open System
open System.IO
open System.IO.Compression
open System.Formats.Tar
open System.Threading
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Logging.Abstractions
open Phos.Backup
open Phos.Storage

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private makeTempDir () : string =
    let dir =
        Path.Combine(Path.GetTempPath(), "phos-backup-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(dir) |> ignore
    dir

let private deleteDir (dir: string) =
    try
        if Directory.Exists dir then
            Directory.Delete(dir, true)
    with _ ->
        ()

let private expectOk (r: Result<'T, string>) : 'T =
    match r with
    | Ok v -> v
    | Error e -> failwithf "Expected Ok, got Error: %s" e

let private expectError (r: Result<'T, string>) : string =
    match r with
    | Ok _ -> failwith "Expected Error, got Ok"
    | Error e -> e

let private ensureParentDir (path: string) : unit =
    match Path.GetDirectoryName path with
    | null
    | "" -> ()
    | d -> Directory.CreateDirectory d |> ignore

let private createSqlite (dbPath: string) (ddl: string) : unit =
    ensureParentDir dbPath

    use conn = new SqliteConnection(sprintf "Data Source=%s" dbPath)
    conn.Open()

    use cmd = conn.CreateCommand()
    cmd.CommandText <- ddl
    cmd.ExecuteNonQuery() |> ignore

let private storageOptions (dbPath: string) : StorageOptions =
    { DatabasePath = dbPath
      BusyTimeout = TimeSpan.FromSeconds 2.0
      ReadPoolSize = 4
      CheckpointEvery = 10 }

/// A fixed, deterministic manifest (all fields populated) for roundtrip tests.
let private sampleManifest: Manifest =
    { Format = 1
      CreatedAt = DateTimeOffset(2026, 9, 17, 11, 30, 0, TimeSpan.Zero)
      AppVersion = "1.2.3"
      SchemaVersion = 11L
      OmpVersion = "18.2.4"
      Files =
        [ { Path = "phos.db"
            Sha256 = "abc"
            Size = 123L }
          { Path = "omp/config.yml"
            Sha256 = "def"
            Size = 45L } ]
      PayloadBytes = 168L }

// ---------------------------------------------------------------------------
// Manifest
// ---------------------------------------------------------------------------

[<Fact>]
let ``manifest toJson ofJson roundtrips`` () =
    let json = Manifest.toJson sampleManifest
    let parsed = Manifest.ofJson json
    parsed |> expectOk |> should equal sampleManifest

[<Fact>]
let ``manifest verify ok on intact file`` () =
    let dir = makeTempDir ()

    try
        let file = Path.Combine(dir, "data.txt")
        File.WriteAllText(file, "hello world")
        let sha = Manifest.sha256File file |> expectOk

        let mf: ManifestFile =
            { Path = "data.txt"
              Sha256 = sha
              Size = int64 (FileInfo(file).Length) }

        let m: Manifest =
            { sampleManifest with
                Files = [ mf ]
                PayloadBytes = mf.Size }

        Manifest.verify dir m |> expectOk |> ignore
    finally
        deleteDir dir

[<Fact>]
let ``manifest verify detects corrupted file and names path`` () =
    let dir = makeTempDir ()

    try
        let file = Path.Combine(dir, "data.txt")
        File.WriteAllText(file, "hello world")
        let sha = Manifest.sha256File file |> expectOk

        let mf: ManifestFile =
            { Path = "data.txt"
              Sha256 = sha
              Size = int64 (FileInfo(file).Length) }

        let m: Manifest =
            { sampleManifest with
                Files = [ mf ]
                PayloadBytes = mf.Size }

        // Flip a byte in place so the size is unchanged and only the checksum
        // differs — verify must reject and name the offending path.
        let bytes = File.ReadAllBytes file
        bytes.[0] <- bytes.[0] ^^^ 0xFFuy
        File.WriteAllBytes(file, bytes)

        let err = Manifest.verify dir m |> expectError
        err.Contains "data.txt" |> should be True
    finally
        deleteDir dir

// ---------------------------------------------------------------------------
// Snapshot (VACUUM INTO)
// ---------------------------------------------------------------------------

[<Fact>]
let ``snapshot sqlite produces consistent WAL snapshot`` () =
    task {
        let dir = makeTempDir ()

        try
            let src = Path.Combine(dir, "src.db")
            let dest = Path.Combine(dir, "dest.db")

            // Source db in WAL mode with a table and pre-writer rows.
            use conn = new SqliteConnection(sprintf "Data Source=%s" src)
            conn.Open()

            use cmd = conn.CreateCommand()
            cmd.CommandText <- "PRAGMA journal_mode=WAL;"
            cmd.ExecuteScalar() |> ignore

            use cmd2 = conn.CreateCommand()
            cmd2.CommandText <- "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);"
            cmd2.ExecuteNonQuery() |> ignore

            for i in 1..5 do
                use cmd = conn.CreateCommand()
                cmd.CommandText <- sprintf "INSERT INTO t(v) VALUES('pre-%d');" i
                cmd.ExecuteNonQuery() |> ignore

            // Concurrent writer: inserts a row every ~10ms for ~200ms while the
            // snapshot runs. A short head start guarantees it is live during the
            // VACUUM INTO.
            let writer =
                task {
                    let deadline = DateTimeOffset.UtcNow.AddMilliseconds 200.0
                    use wconn = new SqliteConnection(sprintf "Data Source=%s" src)
                    wconn.Open()

                    use wcmd = wconn.CreateCommand()
                    wcmd.CommandText <- "PRAGMA busy_timeout=2000;"
                    wcmd.ExecuteNonQuery() |> ignore

                    let mutable n = 0

                    while DateTimeOffset.UtcNow < deadline do
                        n <- n + 1

                        use cmd = wconn.CreateCommand()
                        cmd.CommandText <- sprintf "INSERT INTO t(v) VALUES('post-%d');" n
                        cmd.ExecuteNonQuery() |> ignore
                        do! Task.Delay 10
                }

            do! Task.Delay 20
            let snapTask = Task.Run(fun () -> Snapshot.snapshotSqlite src dest)
            do! writer
            let! snap = snapTask
            snap |> expectOk |> ignore

            // Destination is a valid, consistent snapshot: integrity ok and all
            // pre-writer rows present.
            use dconn = new SqliteConnection(sprintf "Data Source=%s" dest)
            dconn.Open()

            use dcmd = dconn.CreateCommand()
            dcmd.CommandText <- "PRAGMA integrity_check;"

            match dcmd.ExecuteScalar() with
            | :? string as s -> s |> should equal "ok"
            | _ -> failwith "integrity_check returned non-string"

            use dcmd2 = dconn.CreateCommand()
            dcmd2.CommandText <- "SELECT COUNT(*) FROM t WHERE v LIKE 'pre-%';"

            match dcmd2.ExecuteScalar() with
            | :? int64 as n -> n |> should equal 5L
            | _ -> failwith "count returned non-int64"
        finally
            deleteDir dir
    }

// ---------------------------------------------------------------------------
// copyVolatile
// ---------------------------------------------------------------------------

[<Fact>]
let ``copy volatile copies stable file`` () =
    task {
        let dir = makeTempDir ()

        try
            let src = Path.Combine(dir, "src.txt")
            let dst = Path.Combine(dir, "dst.txt")
            File.WriteAllText(src, "stable content")

            let! r = Snapshot.copyVolatile src dst 3
            r |> expectOk |> ignore
            File.ReadAllText dst |> should equal "stable content"
        finally
            deleteDir dir
    }

[<Fact>]
let ``copy volatile errors on missing source`` () =
    task {
        let dir = makeTempDir ()

        try
            let src = Path.Combine(dir, "missing.txt")
            let dst = Path.Combine(dir, "dst.txt")

            let! r = Snapshot.copyVolatile src dst 3
            r |> expectError |> ignore
        finally
            deleteDir dir
    }

// ---------------------------------------------------------------------------
// Retention.prune
// ---------------------------------------------------------------------------

[<Fact>]
let ``retention prune keeps newest and leaves other files`` () =
    let dir = makeTempDir ()

    try
        for i in 1..10 do
            let name = sprintf "phos-backup-202601%02d000000.age" i
            File.WriteAllText(Path.Combine(dir, name), "")

        File.WriteAllText(Path.Combine(dir, "other.txt"), "")

        Retention.prune dir 3 |> expectOk |> should equal 7

        let remaining =
            Directory.GetFiles(dir, "phos-backup-*.age")
            |> Array.map Path.GetFileName
            |> Array.sortDescending

        remaining |> should haveLength 3
        remaining.[0] |> should equal "phos-backup-20260110000000.age"
        remaining.[2] |> should equal "phos-backup-20260108000000.age"

        File.Exists(Path.Combine(dir, "other.txt")) |> should be True
    finally
        deleteDir dir

// ---------------------------------------------------------------------------
// Archive.packTarGz
// ---------------------------------------------------------------------------

[<Fact>]
let ``pack tar gz roundtrips entries`` () =
    let dir = makeTempDir ()

    try
        let staging = Path.Combine(dir, "staging")
        Directory.CreateDirectory(Path.Combine(staging, "omp")) |> ignore
        File.WriteAllText(Path.Combine(staging, "phos.db"), "db")
        File.WriteAllText(Path.Combine(staging, "omp", "config.yml"), "cfg")

        let tarPath = Path.Combine(dir, "out.tar.gz")
        Archive.packTarGz staging tarPath |> expectOk |> ignore

        use fs = File.OpenRead tarPath
        use gz = new GZipStream(fs, CompressionMode.Decompress)
        use tr = new TarReader(gz)
        let names = ResizeArray<string>()
        let mutable entry = Option.ofObj (tr.GetNextEntry())

        while entry.IsSome do
            let e = entry.Value

            if e.EntryType = TarEntryType.RegularFile then
                names.Add e.Name

            entry <- Option.ofObj (tr.GetNextEntry())

        names |> Seq.toList |> List.sort |> should equal [ "omp/config.yml"; "phos.db" ]
    finally
        deleteDir dir

// ---------------------------------------------------------------------------
// Error paths (no age needed)
// ---------------------------------------------------------------------------

[<Fact>]
let ``manifest ofJson rejects malformed json`` () =
    Manifest.ofJson "{ not json" |> expectError |> ignore

[<Fact>]
let ``manifest sha256File errors on missing file`` () =
    Manifest.sha256File "/nonexistent/xyz" |> expectError |> ignore

[<Fact>]
let ``snapshot sqlite errors on missing source`` () =
    let dir = makeTempDir ()

    try
        Snapshot.snapshotSqlite (Path.Combine(dir, "no.db")) (Path.Combine(dir, "out.db"))
        |> expectError
        |> ignore
    finally
        deleteDir dir

[<Fact>]
let ``snapshot sqlite errors when destination exists`` () =
    let dir = makeTempDir ()

    try
        let src = Path.Combine(dir, "src.db")
        let dest = Path.Combine(dir, "dest.db")
        createSqlite src "CREATE TABLE t(id INTEGER PRIMARY KEY);"
        File.WriteAllText(dest, "")

        Snapshot.snapshotSqlite src dest |> expectError |> ignore
    finally
        deleteDir dir

[<Fact>]
let ``copy stable errors on missing source`` () =
    Snapshot.copyStable "/nonexistent/a" "/tmp/b" |> expectError |> ignore

[<Fact>]
let ``retention prune is a no-op on missing directory`` () =
    Retention.prune "/nonexistent/dir" 3 |> expectOk |> should equal 0

[<Fact>]
let ``archive pack errors on missing staging`` () =
    Archive.packTarGz "/nonexistent/staging" "/tmp/x.tar.gz"
    |> expectError
    |> ignore

[<Fact>]
let ``archive encrypt errors on missing recipient file`` () =
    Archive.encryptAge "/tmp/in.tar.gz" "/tmp/out.age" "/nonexistent/recipient.pub"
    |> expectError
    |> ignore

// ---------------------------------------------------------------------------
// backup_log repository
// ---------------------------------------------------------------------------

[<Fact>]
let ``backup log records running and finished entries`` () =
    task {
        let dir = makeTempDir ()

        try
            let db = Path.Combine(dir, "log.db")
            let exec = StorageExecutor.Create(storageOptions db)
            Schema.run (storageOptions db)

            try
                let repo: IBackupLogRepository = BackupLogRepository(exec)
                let! id = repo.Start(DateTimeOffset.FromUnixTimeSeconds 1000L)

                let! running = repo.ListRecent 5
                running |> should haveLength 1
                running.[0].Id |> should equal id
                running.[0].Status |> should equal "running"
                running.[0].FinishedAt |> should equal None

                do! repo.Finish id (DateTimeOffset.FromUnixTimeSeconds 1100L) None None "failed" (Some "boom")

                let! entries = repo.ListRecent 5
                entries |> should haveLength 1
                entries.[0].Status |> should equal "failed"
                entries.[0].Path |> should equal None
                entries.[0].Checksum |> should equal None
                entries.[0].Error |> should equal (Some "boom")

                entries.[0].FinishedAt
                |> should equal (Some(DateTimeOffset.FromUnixTimeSeconds 1100L))
            finally
                exec.Dispose()
        finally
            deleteDir dir
    }

[<Fact>]
let ``backup log lists recent newest first`` () =
    task {
        let dir = makeTempDir ()

        try
            let db = Path.Combine(dir, "log.db")
            let exec = StorageExecutor.Create(storageOptions db)
            Schema.run (storageOptions db)

            try
                let repo: IBackupLogRepository = BackupLogRepository(exec)
                let! id1 = repo.Start(DateTimeOffset.FromUnixTimeSeconds 1000L)
                do! repo.Finish id1 (DateTimeOffset.FromUnixTimeSeconds 1010L) (Some "a.age") (Some "sha") "ok" None
                let! id2 = repo.Start(DateTimeOffset.FromUnixTimeSeconds 2000L)
                do! repo.Finish id2 (DateTimeOffset.FromUnixTimeSeconds 2010L) (Some "b.age") (Some "sha") "ok" None

                let! all = repo.ListRecent 10
                all |> should haveLength 2
                all.[0].Id |> should equal id2
                all.[1].Id |> should equal id1

                let! one = repo.ListRecent 1
                one |> should haveLength 1
                one.[0].Id |> should equal id2
            finally
                exec.Dispose()
        finally
            deleteDir dir
    }

[<Fact>]
let ``build staging excludes env wal shm and tmp dirs`` () =
    task {
        let dir = makeTempDir ()

        try
            let hostDb = Path.Combine(dir, "host.db")
            let profile = Path.Combine(dir, "agent")
            let wsRoot = Path.Combine(dir, "ws")
            Directory.CreateDirectory(Path.Combine(profile, "blobs")) |> ignore

            Directory.CreateDirectory(Path.Combine(profile, "sessions", "-tmp-ompcheck"))
            |> ignore

            File.WriteAllText(Path.Combine(profile, "config.yml"), "c")
            File.WriteAllText(Path.Combine(profile, "models.yml"), "m")
            File.WriteAllText(Path.Combine(profile, ".env"), "SECRET=1")
            createSqlite (Path.Combine(profile, "agent.db")) "CREATE TABLE t(id INTEGER PRIMARY KEY);"
            createSqlite (Path.Combine(profile, "models.db")) "CREATE TABLE t(id INTEGER PRIMARY KEY);"
            createSqlite hostDb "CREATE TABLE t(id INTEGER PRIMARY KEY);"
            File.WriteAllText(Path.Combine(profile, "sessions", "-tmp-ompcheck", "s.jsonl"), "{}\n")

            let wsU1 = Path.Combine(wsRoot, "u1")
            Directory.CreateDirectory(Path.Combine(wsU1, "-tmp-skip")) |> ignore
            File.WriteAllText(Path.Combine(wsU1, ".env"), "SECRET=1")
            File.WriteAllText(Path.Combine(wsU1, "x.db-wal"), "wal")
            File.WriteAllText(Path.Combine(wsU1, "x.db-shm"), "shm")
            File.WriteAllText(Path.Combine(wsU1, "keep.txt"), "keep")
            File.WriteAllText(Path.Combine(wsU1, "-tmp-skip", "junk.txt"), "junk")

            let options: BackupOptions =
                { Directory = Path.Combine(dir, "backups")
                  Interval = TimeSpan.FromHours 1.0
                  AgeRecipient = Path.Combine(dir, "missing.pub")
                  RetainCount = 3
                  DatabasePath = hostDb
                  ProfileDir = profile
                  WorkspaceRoot = wsRoot
                  OmpPath = "omp" }

            let! entries = Snapshot.buildStaging options (Path.Combine(dir, "staging")) 0L
            let rels = entries |> expectOk |> List.map (fun (p, _, _) -> p)

            rels |> should contain "phos.db"
            rels |> should contain "omp/agent.db"
            rels |> should contain "omp/models.db"
            rels |> should contain "omp/config.yml"
            rels |> should contain "omp/models.yml"
            rels |> should contain "workspaces/u1/keep.txt"
            (rels |> List.exists (fun p -> p.Contains ".env")) |> should be False
            (rels |> List.exists (fun p -> p.Contains "db-wal")) |> should be False
            (rels |> List.exists (fun p -> p.Contains "db-shm")) |> should be False
            (rels |> List.exists (fun p -> p.Contains "-tmp-")) |> should be False
        finally
            deleteDir dir
    }

// ---------------------------------------------------------------------------
// BackupService failure paths
// ---------------------------------------------------------------------------

/// Creates a minimal host db placeholder, fake profile and options with a
/// missing age recipient file. Returns (hostDb, backupsDir, options).
let private mkBackupOptions (dir: string) : string * string * BackupOptions =
    let hostDb = Path.Combine(dir, "host.db")
    let profile = Path.Combine(dir, "agent")
    let wsRoot = Path.Combine(dir, "ws")
    Directory.CreateDirectory(Path.Combine(profile, "blobs")) |> ignore
    File.WriteAllText(Path.Combine(profile, "config.yml"), "c")
    File.WriteAllText(Path.Combine(profile, "models.yml"), "m")
    createSqlite (Path.Combine(profile, "agent.db")) "CREATE TABLE t(id INTEGER PRIMARY KEY);"
    createSqlite (Path.Combine(profile, "models.db")) "CREATE TABLE t(id INTEGER PRIMARY KEY);"

    let options: BackupOptions =
        { Directory = Path.Combine(dir, "backups")
          Interval = TimeSpan.FromHours 1.0
          AgeRecipient = Path.Combine(dir, "missing.pub")
          RetainCount = 3
          DatabasePath = hostDb
          ProfileDir = profile
          WorkspaceRoot = wsRoot
          OmpPath = "omp" }

    hostDb, Path.Combine(dir, "backups"), options

[<Fact>]
let ``backup service fails when schema missing`` () =
    task {
        let dir = makeTempDir ()

        try
            let hostDb, backupsDir, options = mkBackupOptions dir
            createSqlite hostDb "CREATE TABLE x(id INTEGER PRIMARY KEY);"
            let logDb = Path.Combine(dir, "log.db")
            let exec = StorageExecutor.Create(storageOptions logDb)
            Schema.run (storageOptions logDb)

            try
                let repo: IBackupLogRepository = BackupLogRepository(exec)
                let svc: IBackupService = BackupService(options, repo, NullLogger.Instance)
                let! r = svc.RunNow()
                r |> expectError |> ignore

                let! entries = repo.ListRecent 5
                entries |> should haveLength 1
                entries.[0].Status |> should equal "failed"
                entries.[0].Error |> should not' (equal None)
                Directory.GetFiles(backupsDir, "*.age") |> should haveLength 0
            finally
                exec.Dispose()
        finally
            deleteDir dir
    }

[<Fact>]
let ``backup service fails when recipient file missing`` () =
    task {
        let dir = makeTempDir ()

        try
            let hostDb, backupsDir, options = mkBackupOptions dir
            Schema.run (storageOptions hostDb)
            let logDb = Path.Combine(dir, "log.db")
            let exec = StorageExecutor.Create(storageOptions logDb)
            Schema.run (storageOptions logDb)

            try
                let repo: IBackupLogRepository = BackupLogRepository(exec)
                let svc: IBackupService = BackupService(options, repo, NullLogger.Instance)
                let! r = svc.RunNow()
                r |> expectError |> ignore

                let! entries = repo.ListRecent 5
                entries |> should haveLength 1
                entries.[0].Status |> should equal "failed"
                entries.[0].Error |> should not' (equal None)
                Directory.GetFiles(backupsDir, "*.age") |> should haveLength 0
            finally
                exec.Dispose()
        finally
            deleteDir dir
    }

// ---------------------------------------------------------------------------
// BackupHostedService
// ---------------------------------------------------------------------------

type private FakeBackupService(behavior: unit -> Task<Result<unit, string>>) =
    interface IBackupService with
        member _.RunNow() = behavior ()

let private hostedOptions: BackupOptions =
    { Directory = "/tmp/whatever"
      Interval = TimeSpan.FromHours 1.0
      AgeRecipient = "/tmp/r"
      RetainCount = 3
      DatabasePath = "/tmp/d"
      ProfileDir = "/tmp/p"
      WorkspaceRoot = "/tmp/w"
      OmpPath = "omp" }

[<Fact>]
let ``hosted service runs startup backup and stops on cancelled token`` () =
    task {
        let calls = ref 0

        let svc =
            new BackupHostedService(
                FakeBackupService(fun () ->
                    task {
                        calls.Value <- calls.Value + 1
                        return Ok()
                    }),
                hostedOptions,
                NullLogger.Instance
            )

        use cts = new CancellationTokenSource()
        cts.Cancel()
        do! svc.ExecuteAsync(cts.Token)
        calls.Value |> should equal 1
    }

[<Fact>]
let ``hosted service continues after failed startup backup`` () =
    task {
        let calls = ref 0

        let svc =
            new BackupHostedService(
                FakeBackupService(fun () ->
                    task {
                        calls.Value <- calls.Value + 1
                        return Error "boom"
                    }),
                hostedOptions,
                NullLogger.Instance
            )

        use cts = new CancellationTokenSource()
        cts.Cancel()
        do! svc.ExecuteAsync(cts.Token)
        calls.Value |> should equal 1
    }

[<Fact>]
let ``hosted service survives throwing startup backup`` () =
    task {
        let svc =
            new BackupHostedService(
                FakeBackupService(fun () -> task { return raise (exn "boom") }),
                hostedOptions,
                NullLogger.Instance
            )

        use cts = new CancellationTokenSource()
        cts.Cancel()
        do! svc.ExecuteAsync(cts.Token)
    }

[<Fact>]
let ``hosted service runs periodically until cancelled`` () =
    task {
        let calls = ref 0

        let svc =
            new BackupHostedService(
                FakeBackupService(fun () ->
                    task {
                        calls.Value <- calls.Value + 1
                        return Ok()
                    }),
                { hostedOptions with
                    Interval = TimeSpan.FromMilliseconds 50.0 },
                NullLogger.Instance
            )

        use cts = new CancellationTokenSource()
        let exec = svc.ExecuteAsync(cts.Token)
        do! Task.Delay 200
        cts.Cancel()
        do! exec
        calls.Value |> should be (greaterThan 1)
    }
