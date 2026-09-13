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

/// App-wide configuration bound from `IConfiguration` (appsettings.json +
/// `PHOS_` environment variables + command line).
[<CLIMutable>]
type AppConfig =
    { Telegram: TelegramSettings
      Storage: StorageSettings
      Whitelist: WhitelistSettings }

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
            return cfg
        }

    /// Binds and validates `AppConfig` from `configuration`. A configuration
    /// with no matching sections (e.g. no `appsettings.json` and no `PHOS_` env
    /// vars) yields an `Error` describing the missing file.
    let bind (configuration: IConfiguration) : Result<AppConfig, string> =
        let sections =
            [ "Telegram"; "Storage"; "Whitelist" ]
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
