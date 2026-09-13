namespace Phos.App

open System
open FsToolkit.ErrorHandling
open Microsoft.Extensions.Configuration
open Phos.Core.DomainTypes
open Phos.Core.Whitelist
open Phos.Storage

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

/// App-wide configuration bound from `IConfiguration` (appsettings.json +
/// `PHOS_` environment variables + command line).
[<CLIMutable>]
type AppConfig =
    { Telegram: TelegramSettings
      Storage: StorageSettings
      Whitelist: WhitelistSettings
      Stt: SttSettings }

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
            [ "Telegram"; "Storage"; "Whitelist"; "Stt" ]
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
