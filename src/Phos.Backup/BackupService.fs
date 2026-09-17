namespace Phos.Backup

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Phos.Storage

/// Runs a single backup pass. Implemented by `BackupService`; the hosted
/// service and tests drive it through this boundary.
type IBackupService =
    abstract RunNow: unit -> Task<Result<unit, string>>

/// Orchestrates one backup run: snapshot staging, manifest, tar.gz + age
/// encryption, atomic publish to the backup directory, `backup_log` bookkeeping
/// and retention pruning.
type BackupService(options: BackupOptions, log: IBackupLogRepository, logger: ILogger) =
    // Serializes concurrent RunNow calls: a second concurrent call fails fast.
    let gate = new SemaphoreSlim(1, 1)

    /// Captures the OMP version for diagnostics (informational only).
    let ompVersion () : string =
        try
            let psi = ProcessStartInfo(options.OmpPath)
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            psi.ArgumentList.Add "--version"

            use proc = new Process()
            proc.StartInfo <- psi

            if proc.Start() then
                let stdoutTask = proc.StandardOutput.ReadToEndAsync()
                let _ = proc.StandardError.ReadToEndAsync()

                if proc.WaitForExit(5000) then
                    stdoutTask.Result.Trim()
                else
                    try
                        proc.Kill(entireProcessTree = true)
                    with _ ->
                        ()

                    ""
            else
                ""
        with _ ->
            ""

    /// Reads `COALESCE(MAX(Version), 0)` from `VersionInfo` on a fresh
    /// connection. A missing `VersionInfo` table is an `Error`.
    member private this.getSchemaVersion() : Task<Result<int64, string>> =
        task {
            try
                let csb = SqliteConnectionStringBuilder()
                csb.DataSource <- options.DatabasePath
                csb.Mode <- SqliteOpenMode.ReadWrite

                use conn = new SqliteConnection(csb.ConnectionString)
                conn.Open()

                use cmd = conn.CreateCommand()
                cmd.CommandText <- "SELECT COALESCE(MAX(Version), 0) FROM VersionInfo;"

                match cmd.ExecuteScalar() with
                | :? int64 as v -> return Ok v
                | :? int as v -> return Ok(int64 v)
                | null -> return Ok 0L
                | other -> return Error(sprintf "unexpected VersionInfo result: %O" other)
            with ex ->
                return Error ex.Message
        }

    /// Computes the destination archive path `phos-backup-<yyyyMMdd-HHmmss>.age`
    /// in the backup directory, appending `-<n>` on a name collision.
    member private this.uniqueFinalPath() : string =
        let baseName = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")

        let candidate =
            Path.Combine(options.Directory, sprintf "phos-backup-%s.age" baseName)

        if not (File.Exists candidate) then
            candidate
        else
            let mutable n = 1

            let mutable next =
                Path.Combine(options.Directory, sprintf "phos-backup-%s-%d.age" baseName n)

            while File.Exists next do
                n <- n + 1
                next <- Path.Combine(options.Directory, sprintf "phos-backup-%s-%d.age" baseName n)

            next

    /// Builds the staging tree, manifest, tar.gz and encrypted archive. Returns
    /// `Ok(finalPath, checksum)` on success. Staging and temp files are removed
    /// in `finally` regardless of outcome.
    member private this.doBackup(schemaVersion: int64) : Task<Result<string * string, string>> =
        task {
            let stagingDir =
                Path.Combine(Path.GetTempPath(), sprintf "phos-backup-staging-%s" (Guid.NewGuid().ToString "N"))

            let tarPath =
                Path.Combine(Path.GetTempPath(), sprintf "phos-backup-%s.tar.gz" (Guid.NewGuid().ToString "N"))

            let encryptedTmp =
                Path.Combine(options.Directory, sprintf ".phos-backup-%s.age.tmp" (Guid.NewGuid().ToString "N"))

            try
                try
                    let! files = Snapshot.buildStaging options stagingDir schemaVersion

                    match files with
                    | Error e -> return Error e
                    | Ok entries ->
                        let appVersion =
                            let asm = typeof<BackupService>.Assembly

                            asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                            |> Option.ofObj
                            |> Option.map (fun a -> a.InformationalVersion)
                            |> Option.defaultValue "0.0.0"

                        let manifest =
                            { Format = 1
                              CreatedAt = DateTimeOffset.UtcNow
                              AppVersion = appVersion
                              SchemaVersion = schemaVersion
                              OmpVersion = ompVersion ()
                              Files = entries |> List.map (fun (p, sz, sha) -> { Path = p; Sha256 = sha; Size = sz })
                              PayloadBytes = entries |> List.sumBy (fun (_, sz, _) -> sz) }

                        File.WriteAllText(Path.Combine(stagingDir, "manifest.json"), Manifest.toJson manifest)

                        match Archive.packTarGz stagingDir tarPath with
                        | Error e -> return Error e
                        | Ok() ->
                            match Archive.encryptAge tarPath encryptedTmp options.AgeRecipient with
                            | Error e -> return Error e
                            | Ok() ->
                                let finalPath = this.uniqueFinalPath ()
                                File.Move(encryptedTmp, finalPath)

                                match Manifest.sha256File finalPath with
                                | Ok checksum -> return Ok(finalPath, checksum)
                                | Error e -> return Error e
                with ex ->
                    return Error ex.Message
            finally
                try
                    if Directory.Exists stagingDir then
                        Directory.Delete(stagingDir, true)
                with _ ->
                    ()

                try
                    if File.Exists tarPath then
                        File.Delete tarPath
                with _ ->
                    ()

                try
                    if File.Exists encryptedTmp then
                        File.Delete encryptedTmp
                with _ ->
                    ()
        }

    /// Runs a full backup pass: ensures the backup directory, writes the
    /// `running` log row, snapshots, publishes, then records `ok` or `failed`.
    member private this.runBackup() : Task<Result<unit, string>> =
        task {
            try
                Directory.CreateDirectory options.Directory |> ignore
                let! id = log.Start(DateTimeOffset.UtcNow)

                try
                    let! schemaResult = this.getSchemaVersion ()

                    match schemaResult with
                    | Error e ->
                        do! log.Finish id (DateTimeOffset.UtcNow) None None "failed" (Some e)
                        logger.LogError("backup failed: {Error}", e)
                        return Error e
                    | Ok schemaVersion ->
                        let! outcome = this.doBackup schemaVersion

                        match outcome with
                        | Ok(finalPath, checksum) ->
                            do! log.Finish id (DateTimeOffset.UtcNow) (Some finalPath) (Some checksum) "ok" None
                            logger.LogInformation("backup ok: {Path}", finalPath)

                            match Retention.prune options.Directory options.RetainCount with
                            | Ok deleted -> logger.LogInformation("retention pruned {Count} old backup(s)", deleted)
                            | Error e -> logger.LogWarning("retention prune failed: {Error}", e)

                            return Ok()
                        | Error e ->
                            do! log.Finish id (DateTimeOffset.UtcNow) None None "failed" (Some e)
                            logger.LogError("backup failed: {Error}", e)
                            return Error e
                with ex ->
                    do! log.Finish id (DateTimeOffset.UtcNow) None None "failed" (Some ex.Message)
                    logger.LogError(ex, "backup failed")
                    return Error ex.Message
            with ex ->
                logger.LogError(ex, "backup failed")
                return Error ex.Message
        }

    interface IBackupService with
        member this.RunNow() =
            task {
                if not (gate.Wait(0)) then
                    return Error "backup already in progress"
                else
                    try
                        return! this.runBackup ()
                    finally
                        gate.Release() |> ignore
            }

/// Background loop that runs a backup at startup, then every `options.Interval`.
type BackupHostedService(service: IBackupService, options: BackupOptions, logger: ILogger) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) : Task =
        task {
            // Run once at startup; log failures and continue.
            try
                let! result = service.RunNow()

                match result with
                | Ok _ -> logger.LogInformation("startup backup ok")
                | Error e -> logger.LogError("startup backup failed: {Error}", e)
            with ex ->
                logger.LogError(ex, "startup backup failed")

            use timer = new PeriodicTimer(options.Interval)

            try
                let mutable running = true

                while running && not stoppingToken.IsCancellationRequested do
                    let! tick = timer.WaitForNextTickAsync(stoppingToken)

                    if tick then
                        try
                            let! result = service.RunNow()

                            match result with
                            | Ok _ -> logger.LogInformation("backup ok")
                            | Error e -> logger.LogError("backup failed: {Error}", e)
                        with ex ->
                            logger.LogError(ex, "backup failed")
                    else
                        running <- false
            with :? OperationCanceledException ->
                ()
        }
