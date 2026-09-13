namespace Phos.App

open System
open System.IO
open System.Text.Json
open FsToolkit.ErrorHandling
open Phos.Core.DomainTypes
open Phos.Core.Whitelist
open Phos.Storage

/// Telegram credentials loaded from the environment.
[<CLIMutable>]
type Secrets =
    { ApiId: int
      ApiHash: string
      BotToken: string }

/// A single user entry in the config file.
[<CLIMutable>]
type ConfigUser = { Id: int64; Role: string }

/// App-wide configuration loaded from the JSON file.
[<CLIMutable>]
type AppConfig =
    { DatabasePath: string
      BusyTimeoutSeconds: int
      ReadPoolSize: int
      CheckpointEvery: int
      SessionPath: string
      Users: ConfigUser list
      AllowedChats: int64 list }

/// Loads and validates the app configuration.
module Config =

    /// True when `role` is one of the accepted whitelist roles (case-insensitive).
    let private validRole (role: string) : bool =
        match role.Trim().ToLowerInvariant() with
        | "owner"
        | "admin"
        | "user" -> true
        | _ -> false

    /// Validates every user role against the allowed set.
    let private validateRoles (users: ConfigUser list) : Result<unit, string> =
        match users |> List.tryFind (fun u -> not (validRole u.Role)) with
        | Some u -> Error(sprintf "unknown role for user %d: %s" u.Id u.Role)
        | None -> Ok()

    /// Validates the loaded config fields, returning a typed error on failure.
    let private validate (cfg: AppConfig) : Result<AppConfig, string> =
        result {
            do! Result.requireTrue "DatabasePath must not be empty" (not (String.IsNullOrWhiteSpace cfg.DatabasePath))
            do! Result.requireTrue "SessionPath must not be empty" (not (String.IsNullOrWhiteSpace cfg.SessionPath))
            do! Result.requireTrue "BusyTimeoutSeconds must be >= 1" (cfg.BusyTimeoutSeconds >= 1)
            do! Result.requireTrue "ReadPoolSize must be >= 1" (cfg.ReadPoolSize >= 1)
            do! Result.requireTrue "CheckpointEvery must be >= 0" (cfg.CheckpointEvery >= 0)
            do! validateRoles cfg.Users
            return cfg
        }

    /// Loads the app config from `path`. A missing file or invalid JSON yields
    /// an `Error` describing the problem.
    let load (path: string) : Result<AppConfig, string> =
        if not (File.Exists path) then
            Error(sprintf "config file not found: %s" path)
        else
            try
                let json = File.ReadAllText path
                let options = JsonSerializerOptions()
                options.PropertyNameCaseInsensitive <- true

                let cfg: AppConfig | null = JsonSerializer.Deserialize<AppConfig>(json, options)

                match cfg with
                | null -> Error "config file is empty"
                | cfg -> validate cfg
            with :? JsonException as ex ->
                Error(sprintf "invalid config JSON: %s" ex.Message)

    /// Loads Telegram credentials from the environment. A missing variable
    /// yields an `Error` naming the exact variable.
    let loadSecrets () : Result<Secrets, string> =
        result {
            let! apiIdStr =
                Environment.GetEnvironmentVariable "PHOS_TELEGRAM_API_ID"
                |> Option.ofObj
                |> Result.requireSome "PHOS_TELEGRAM_API_ID is not set"

            let! apiHash =
                Environment.GetEnvironmentVariable "PHOS_TELEGRAM_API_HASH"
                |> Option.ofObj
                |> Result.requireSome "PHOS_TELEGRAM_API_HASH is not set"

            let! botToken =
                Environment.GetEnvironmentVariable "PHOS_TELEGRAM_BOT_TOKEN"
                |> Option.ofObj
                |> Result.requireSome "PHOS_TELEGRAM_BOT_TOKEN is not set"

            let! apiId =
                match Int32.TryParse apiIdStr with
                | true, v -> Ok v
                | false, _ -> Error "PHOS_TELEGRAM_API_ID must be an integer"

            return
                { ApiId = apiId
                  ApiHash = apiHash
                  BotToken = botToken }
        }

    /// Builds a `Whitelist` from the config users and allowed chats. An unknown
    /// role yields an `Error` naming the offending user id.
    let toWhitelist (cfg: AppConfig) : Result<Whitelist, string> =
        let buildUsers (users: ConfigUser list) : Result<Map<UserId, UserRole>, string> =
            users
            |> List.traverseResultM (fun u ->
                match u.Role.Trim().ToLowerInvariant() with
                | "owner" -> Ok(UserId u.Id, Owner)
                | "admin" -> Ok(UserId u.Id, Admin)
                | "user" -> Ok(UserId u.Id, User)
                | role -> Error(sprintf "unknown role for user %d: %s" u.Id role))
            |> Result.map Map.ofList

        result {
            let! users = buildUsers cfg.Users
            let allowedChats = cfg.AllowedChats |> List.map ChatId |> Set.ofList
            return create users allowedChats
        }

    /// Builds storage options from the config.
    let toStorageOptions (cfg: AppConfig) : StorageOptions =
        { DatabasePath = cfg.DatabasePath
          BusyTimeout = TimeSpan.FromSeconds(float cfg.BusyTimeoutSeconds)
          ReadPoolSize = cfg.ReadPoolSize
          CheckpointEvery = cfg.CheckpointEvery }
