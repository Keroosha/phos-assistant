module Phos.App.Program

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Phos.Core.DomainTypes
open Phos.Core.Whitelist
open Phos.Storage
open Phos.Telegram
open Phos.App

/// Runs the host: ensures directories, initializes storage, wires the update
/// handler, logs in to Telegram, starts the outbox delivery loop, and blocks
/// until Ctrl+C.
let private runHost (configPath: string) (cfg: AppConfig) (secrets: Secrets) (whitelist: Whitelist) : int =
    use loggerFactory = LoggerFactory.Create(fun b -> b.AddSimpleConsole() |> ignore)
    let logger = loggerFactory.CreateLogger("Phos")

    try
        logger.LogInformation("config loaded from {ConfigPath}", configPath)

        Transport.ensureSessionDir cfg.SessionPath

        match Path.GetDirectoryName cfg.DatabasePath with
        | null
        | "" -> ()
        | dir -> Directory.CreateDirectory dir |> ignore

        let options = Config.toStorageOptions cfg

        use exec = StorageExecutor.Create options
        Schema.run options
        logger.LogInformation("storage ready at {DatabasePath}", cfg.DatabasePath)

        let inbox = CommandInbox(exec) :> ICommandInbox
        let outbox = MessageOutbox(exec) :> IMessageOutbox
        let users = UserRepository(exec) :> IUserRepository

        let dedupe = UpdateDedupe 1000

        let admit (env: CommandEnvelope) : Task<AdmitOutcome> =
            task {
                try
                    let! id = inbox.Insert env
                    return Admitted id
                with ex ->
                    logger.LogError(ex, "admit failed")
                    return Failed
            }

        let enqueueOutbox (env: OutboxEnvelope) : Task<unit> =
            task {
                let! _ = outbox.Insert env.CommandId env.ChunkIndex env.ChatId env.RandomId env.Payload
                return ()
            }

        let handler = UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueueOutbox)

        let transport =
            TelegramTransport
                { ApiId = secrets.ApiId
                  ApiHash = secrets.ApiHash
                  BotToken = secrets.BotToken
                  SessionPath = cfg.SessionPath }

        let handleUpdate (u: IncomingUpdate) : Task<unit> =
            task {
                let! _ = handler.HandleAsync u
                return ()
            }

        transport.OnUpdate(fun updates ->
            task {
                for u in UpdateModel.mapUpdates updates do
                    try
                        do! handleUpdate u
                    with ex ->
                        logger.LogError(ex, "handle update failed")
            })

        let botInfo = (transport :> ITelegramTransport).Login().GetAwaiter().GetResult()
        logger.LogInformation("phos ready, bot id {BotId}", botInfo.BotId)

        let cts = new CancellationTokenSource()
        let delivery = OutboxDelivery(outbox, transport :> ITelegramTransport, logger)
        let deliveryTask = delivery.RunAsync cts.Token
        logger.LogInformation("delivery loop running")

        Console.CancelKeyPress.Add(fun e ->
            e.Cancel <- true
            cts.Cancel())

        cts.Token.WaitHandle.WaitOne() |> ignore

        try
            deliveryTask.Wait(TimeSpan.FromSeconds 5.0) |> ignore
        with :? AggregateException ->
            // Cancellation faults the Task.Delay inside the delivery loop; the
            // loop is already being torn down, so a clean shutdown proceeds.
            ()

        0
    with ex ->
        logger.LogError(ex, "host failed")
        eprintfn "phos: %s" ex.Message
        1

/// Entry point. Resolves the config path, loads and validates config/secrets/
/// whitelist, then runs the host. Any load error is printed to stderr and
/// returns 1.
[<EntryPoint>]
let main (argv: string[]) : int =
    let configPath =
        if argv.Length > 0 then
            argv.[0]
        else
            Environment.GetEnvironmentVariable "PHOS_CONFIG"
            |> Option.ofObj
            |> Option.defaultValue "phos.json"

    match Config.load configPath with
    | Error msg ->
        eprintfn "phos: %s" msg
        1
    | Ok cfg ->
        match Config.loadSecrets () with
        | Error msg ->
            eprintfn "phos: %s" msg
            1
        | Ok secrets ->
            match Config.toWhitelist cfg with
            | Error msg ->
                eprintfn "phos: %s" msg
                1
            | Ok whitelist -> runHost configPath cfg secrets whitelist
