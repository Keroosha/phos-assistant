namespace Phos.App

open System
open System.IO
open FsToolkit.ErrorHandling
open Microsoft.Extensions.Configuration
open Phos.Core.DomainTypes
open Phos.Core.ScheduleJobs
open Phos.Core.Whitelist
open Phos.Storage
open Phos.Scheduler
open Phos.Omp

/// Telegram connection settings, bound from the `Telegram` config section.
[<CLIMutable>]
type TelegramSettings =
    { ApiId: int
      ApiHash: string
      BotToken: string
      SessionPath: string }

/// SQLite storage settings, bound from the `Storage` config section.
[<CLIMutable>]
type StorageSettings =
    { DatabasePath: string
      BusyTimeoutSeconds: int
      ReadPoolSize: int
      CheckpointEvery: int }

/// A single user entry in the whitelist config.
[<CLIMutable>]
type ConfigUser = { Id: int64; Role: string }

/// Whitelist settings: allowed users and explicitly allowed chats.
[<CLIMutable>]
type WhitelistSettings =
    { Users: ConfigUser array
      AllowedChats: int64 array }

/// Speech-to-text settings, bound from the `Stt` config section. `WhisperDir`
/// is optional; an empty string disables the Whisper fallback engine.
[<CLIMutable>]
type SttSettings =
    { Enabled: bool
      ModelPath: string
      TokensPath: string
      VadModelPath: string
      WhisperDir: string
      NumThreads: int
      MaxBytes: int64
      MaxDurationSeconds: int
      FfmpegPath: string
      FfmpegTimeoutSeconds: int
      ProbeTimeoutSeconds: int
      MaxConcurrentStt: int
      ModelSha256: string }

/// OMP session-manager settings, bound from the `Omp` config section.
[<CLIMutable>]
type OmpSettings =
    { Enabled: bool
      Profile: string
      SourceProfile: string
      OmpPath: string
      WorkspaceRoot: string
      PersonaFile: string
      IdleTimeoutMinutes: int
      MaxQueuePerUser: int
      Tools: string
      ApprovalMode: string
      MaxTime: string
      ReadyTimeoutSeconds: int }

/// Scheduler settings, bound from the `Scheduler` config section.
[<CLIMutable>]
type SchedulerSettings =
    { MaxJobsPerUser: int
      MinIntervalSeconds: int
      MaxPromptLength: int
      MaxFailedTicks: int
      TickSeconds: int
      PendingTtlHours: int }

/// Backup settings, bound from the `Backup` config section. `AgeRecipient`
/// points to a public age key file held outside the archive; it is only
/// required when `Enabled` is true.
[<CLIMutable>]
type BackupSettings =
    { Enabled: bool
      Directory: string
      Interval: TimeSpan
      AgeRecipient: string
      RetainCount: int }

/// App-wide configuration bound from `IConfiguration` (appsettings.json +
/// `PHOS_` environment variables + command line).
[<CLIMutable>]
type AppConfig =
    { Telegram: TelegramSettings
      Storage: StorageSettings
      Whitelist: WhitelistSettings
      Stt: SttSettings
      Omp: OmpSettings
      Scheduler: SchedulerSettings
      Backup: BackupSettings }

/// Binds and validates the app configuration.
module Config =

    /// True when `role` is one of the accepted whitelist roles (case-insensitive).
    let private validRole (role: string) : bool =
        match role.Trim().ToLowerInvariant() with
        | "owner"
        | "admin"
        | "user" -> true
        | _ -> false

    /// Validates every user role against the allowed set.
    let private validateRoles (users: ConfigUser array) : Result<unit, string> =
        match users |> Array.tryFind (fun u -> not (validRole u.Role)) with
        | Some u -> Error(sprintf "unknown role for user %d: %s" u.Id u.Role)
        | None -> Ok()

    /// Validates the bound config fields, returning a typed error on failure.
    /// Error strings name the offending key and, for secrets, the env var that
    /// must be set to supply it.
    let private validate (cfg: AppConfig) : Result<AppConfig, string> =
        result {
            do!
                Result.requireTrue
                    "Storage:DatabasePath must not be empty"
                    (not (String.IsNullOrWhiteSpace cfg.Storage.DatabasePath))

            do! Result.requireTrue "Storage:BusyTimeoutSeconds must be >= 1" (cfg.Storage.BusyTimeoutSeconds >= 1)
            do! Result.requireTrue "Storage:ReadPoolSize must be >= 1" (cfg.Storage.ReadPoolSize >= 1)
            do! Result.requireTrue "Storage:CheckpointEvery must be >= 0" (cfg.Storage.CheckpointEvery >= 0)

            do!
                Result.requireTrue
                    "Telegram:SessionPath must not be empty"
                    (not (String.IsNullOrWhiteSpace cfg.Telegram.SessionPath))

            do! Result.requireTrue "Telegram:ApiId must be > 0 (set PHOS_TELEGRAM__APIID)" (cfg.Telegram.ApiId > 0)

            do!
                Result.requireTrue
                    "Telegram:ApiHash must not be empty (set PHOS_TELEGRAM__APIHASH)"
                    (not (String.IsNullOrWhiteSpace cfg.Telegram.ApiHash))

            do!
                Result.requireTrue
                    "Telegram:BotToken must not be empty (set PHOS_TELEGRAM__BOTTOKEN)"
                    (not (String.IsNullOrWhiteSpace cfg.Telegram.BotToken))

            do! validateRoles cfg.Whitelist.Users

            if cfg.Omp.Enabled then
                do! Result.requireTrue "Omp:Profile must not be empty" (not (String.IsNullOrWhiteSpace cfg.Omp.Profile))

                do! Result.requireTrue "Omp:OmpPath must not be empty" (not (String.IsNullOrWhiteSpace cfg.Omp.OmpPath))

                do!
                    Result.requireTrue
                        "Omp:WorkspaceRoot must not be empty"
                        (not (String.IsNullOrWhiteSpace cfg.Omp.WorkspaceRoot))

                do! Result.requireTrue "Omp:IdleTimeoutMinutes must be >= 1" (cfg.Omp.IdleTimeoutMinutes >= 1)
                do! Result.requireTrue "Omp:MaxQueuePerUser must be >= 1" (cfg.Omp.MaxQueuePerUser >= 1)
                do! Result.requireTrue "Omp:ReadyTimeoutSeconds must be >= 1" (cfg.Omp.ReadyTimeoutSeconds >= 1)

            do! Result.requireTrue "Scheduler:MaxJobsPerUser must be >= 1" (cfg.Scheduler.MaxJobsPerUser >= 1)
            do! Result.requireTrue "Scheduler:MinIntervalSeconds must be >= 1" (cfg.Scheduler.MinIntervalSeconds >= 1)
            do! Result.requireTrue "Scheduler:MaxPromptLength must be >= 1" (cfg.Scheduler.MaxPromptLength >= 1)
            do! Result.requireTrue "Scheduler:MaxFailedTicks must be >= 1" (cfg.Scheduler.MaxFailedTicks >= 1)
            do! Result.requireTrue "Scheduler:TickSeconds must be >= 1" (cfg.Scheduler.TickSeconds >= 1)
            do! Result.requireTrue "Scheduler:PendingTtlHours must be >= 1" (cfg.Scheduler.PendingTtlHours >= 1)

            if cfg.Backup.Enabled then
                do!
                    Result.requireTrue
                        "Backup:Directory must not be empty"
                        (not (String.IsNullOrWhiteSpace cfg.Backup.Directory))

                do!
                    Result.requireTrue
                        "Backup:AgeRecipient must not be empty"
                        (not (String.IsNullOrWhiteSpace cfg.Backup.AgeRecipient))

                do! Result.requireTrue "Backup:Interval must be > 0" (cfg.Backup.Interval > TimeSpan.Zero)

                do!
                    Result.requireTrue
                        "Backup:Interval must be <= 30 days"
                        (cfg.Backup.Interval <= TimeSpan.FromDays 30.0)

                do! Result.requireTrue "Backup:RetainCount must be >= 1" (cfg.Backup.RetainCount >= 1)

            if cfg.Stt.Enabled then
                do!
                    Result.requireTrue
                        "Stt:ModelPath must not be empty"
                        (not (String.IsNullOrWhiteSpace cfg.Stt.ModelPath))

                do!
                    Result.requireTrue
                        "Stt:TokensPath must not be empty"
                        (not (String.IsNullOrWhiteSpace cfg.Stt.TokensPath))

                do!
                    Result.requireTrue
                        "Stt:VadModelPath must not be empty"
                        (not (String.IsNullOrWhiteSpace cfg.Stt.VadModelPath))

                do! Result.requireTrue "Stt:NumThreads must be >= 1" (cfg.Stt.NumThreads >= 1)
                do! Result.requireTrue "Stt:MaxBytes must be > 0" (cfg.Stt.MaxBytes > 0L)
                do! Result.requireTrue "Stt:MaxDurationSeconds must be > 0" (cfg.Stt.MaxDurationSeconds > 0)
                do! Result.requireTrue "Stt:MaxConcurrentStt must be >= 1" (cfg.Stt.MaxConcurrentStt >= 1)
                do! Result.requireTrue "Stt:FfmpegTimeoutSeconds must be > 0" (cfg.Stt.FfmpegTimeoutSeconds > 0)

            return cfg
        }

    /// Binds and validates `AppConfig` from `configuration`. A configuration
    /// with no matching sections (e.g. no `appsettings.json` and no `PHOS_` env
    /// vars) yields an `Error` describing the missing file.
    let bind (configuration: IConfiguration) : Result<AppConfig, string> =
        let sections =
            [ "Telegram"; "Storage"; "Whitelist"; "Stt"; "Omp"; "Scheduler"; "Backup" ]
            |> List.forall (fun name -> configuration.GetSection(name).Exists())

        if not sections then
            Error "configuration is empty (appsettings.json not found)"
        else
            match configuration.Get<AppConfig>() with
            | null -> Error "configuration is empty (appsettings.json not found)"
            | cfg -> validate cfg

    /// Builds a `Whitelist` from the bound config users and allowed chats. An
    /// unknown role yields an `Error` naming the offending user id.
    let toWhitelist (cfg: AppConfig) : Result<Whitelist, string> =
        let buildUsers (users: ConfigUser array) : Result<Map<UserId, UserRole>, string> =
            users
            |> Array.toList
            |> List.traverseResultM (fun u ->
                match u.Role.Trim().ToLowerInvariant() with
                | "owner" -> Ok(UserId u.Id, Owner)
                | "admin" -> Ok(UserId u.Id, Admin)
                | "user" -> Ok(UserId u.Id, User)
                | role -> Error(sprintf "unknown role for user %d: %s" u.Id role))
            |> Result.map Map.ofList

        result {
            let! users = buildUsers cfg.Whitelist.Users

            let allowedChats =
                cfg.Whitelist.AllowedChats |> Array.toList |> List.map ChatId |> Set.ofList

            return create users allowedChats
        }

    /// Builds storage options from the config.
    let toStorageOptions (cfg: AppConfig) : StorageOptions =
        { DatabasePath = cfg.Storage.DatabasePath
          BusyTimeout = TimeSpan.FromSeconds(float cfg.Storage.BusyTimeoutSeconds)
          ReadPoolSize = cfg.Storage.ReadPoolSize
          CheckpointEvery = cfg.Storage.CheckpointEvery }

    /// Expands a leading `~` to the user's home directory (POSIX convention).
    let expandHome (path: string) : string =
        if path = "~" then
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        elif path.StartsWith "~/" then
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring 2)
        else
            path

    /// Builds the base OMP spawn options. `WorkspaceDir` is left empty — it is
    /// per-user and set by `SessionManager` at spawn time.
    let toOmpProcessOptions (cfg: AppConfig) : OmpProcessOptions =
        { OmpPath = cfg.Omp.OmpPath
          Profile = cfg.Omp.Profile
          WorkspaceDir = ""
          SessionResume = None
          Tools = cfg.Omp.Tools
          ApprovalMode = cfg.Omp.ApprovalMode
          MaxTime = cfg.Omp.MaxTime
          ExtraFlags = []
          ReadyTimeoutSeconds = cfg.Omp.ReadyTimeoutSeconds }

    /// Builds the per-user session-manager options from the config.
    let toSessionManagerOptions (cfg: AppConfig) : SessionManagerOptions =
        { Profile = cfg.Omp.Profile
          SourceProfile = cfg.Omp.SourceProfile
          IdleTimeout = TimeSpan.FromMinutes(float cfg.Omp.IdleTimeoutMinutes)
          MaxQueuePerUser = cfg.Omp.MaxQueuePerUser }

    /// Builds STT options from the config. An empty `WhisperDir` disables the
    /// Whisper fallback engine.
    let toSttOptions (cfg: AppConfig) : Phos.Speech.SttOptions =
        { ModelPath = cfg.Stt.ModelPath
          TokensPath = cfg.Stt.TokensPath
          VadModelPath = cfg.Stt.VadModelPath
          WhisperDir =
            if String.IsNullOrWhiteSpace cfg.Stt.WhisperDir then
                None
            else
                Some cfg.Stt.WhisperDir
          NumThreads = cfg.Stt.NumThreads
          MaxBytes = cfg.Stt.MaxBytes
          MaxDurationSeconds = cfg.Stt.MaxDurationSeconds
          FfmpegPath = cfg.Stt.FfmpegPath
          FfmpegTimeoutSeconds = cfg.Stt.FfmpegTimeoutSeconds
          ProbeTimeoutSeconds = cfg.Stt.ProbeTimeoutSeconds
          MaxConcurrentStt = cfg.Stt.MaxConcurrentStt
          ModelSha256 = cfg.Stt.ModelSha256 }

    /// Builds the per-user scheduling quota from the config.
    let toScheduleQuota (cfg: AppConfig) : ScheduleQuota =
        { MaxJobsPerUser = cfg.Scheduler.MaxJobsPerUser
          MinIntervalSeconds = cfg.Scheduler.MinIntervalSeconds
          MaxPromptLength = cfg.Scheduler.MaxPromptLength }

    /// Builds the scheduler runtime options from the config.
    let toSchedulerOptions (cfg: AppConfig) : SchedulerOptions =
        { TickSeconds = cfg.Scheduler.TickSeconds
          PendingTtlHours = cfg.Scheduler.PendingTtlHours
          MaxFailedTicks = cfg.Scheduler.MaxFailedTicks }

    /// Builds backup options from the config. The `AgeRecipient` file existence
    /// is checked at backup time (not at host startup) so a config typo does not
    /// prevent the host from booting.
    let toBackupOptions (cfg: AppConfig) : Phos.Backup.BackupOptions =
        { Directory = expandHome cfg.Backup.Directory
          Interval = cfg.Backup.Interval
          AgeRecipient = expandHome cfg.Backup.AgeRecipient
          RetainCount = cfg.Backup.RetainCount
          DatabasePath = cfg.Storage.DatabasePath
          ProfileDir = Path.Combine(expandHome "~/.omp", "profiles", cfg.Omp.Profile, "agent")
          WorkspaceRoot = expandHome cfg.Omp.WorkspaceRoot
          OmpPath = cfg.Omp.OmpPath }
