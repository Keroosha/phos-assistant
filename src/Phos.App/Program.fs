module Phos.App.Program

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Phos.Core.Whitelist
open Phos.Storage
open Phos.Telegram
open Phos.App

/// Entry point. Binds and validates config (appsettings.json + `PHOS_` env vars
/// + command line), then runs the Generic Host. Any config/load error is printed
/// to stderr and returns 1.
[<EntryPoint>]
let main (argv: string[]) : int =
    let builder = HostApplicationBuilder(argv)
    builder.Configuration.AddEnvironmentVariables("PHOS_") |> ignore

    match Config.bind builder.Configuration with
    | Error msg ->
        eprintfn "phos: %s" msg
        1
    | Ok cfg ->
        match Config.toWhitelist cfg with
        | Error msg ->
            eprintfn "phos: %s" msg
            1
        | Ok whitelist ->
            let options = Config.toStorageOptions cfg

            Transport.ensureSessionDir cfg.Telegram.SessionPath

            match Path.GetDirectoryName cfg.Storage.DatabasePath with
            | null
            | "" -> ()
            | dir -> Directory.CreateDirectory dir |> ignore

            // Migrations must exist before any repository is used.
            Schema.run options

            builder.Logging.AddSimpleConsole() |> ignore

            builder.Services.AddSingleton<AppConfig>(cfg) |> ignore
            builder.Services.AddSingleton<Whitelist>(whitelist) |> ignore
            builder.Services.AddSingleton<StorageOptions>(fun _ -> options) |> ignore

            builder.Services.AddSingleton<StorageExecutor>(fun sp ->
                StorageExecutor.Create(sp.GetRequiredService<StorageOptions>()))
            |> ignore

            builder.Services.AddSingleton<ICommandInbox>(fun sp ->
                CommandInbox(sp.GetRequiredService<StorageExecutor>()) :> ICommandInbox)
            |> ignore

            builder.Services.AddSingleton<IMessageOutbox>(fun sp ->
                MessageOutbox(sp.GetRequiredService<StorageExecutor>()) :> IMessageOutbox)
            |> ignore

            builder.Services.AddSingleton<IUserRepository>(fun sp ->
                UserRepository(sp.GetRequiredService<StorageExecutor>()) :> IUserRepository)
            |> ignore

            builder.Services.AddSingleton<UpdateDedupe>(UpdateDedupe 1000) |> ignore

            builder.Services.AddSingleton<TelegramTransport>(fun _ ->
                TelegramTransport
                    { ApiId = cfg.Telegram.ApiId
                      ApiHash = cfg.Telegram.ApiHash
                      BotToken = cfg.Telegram.BotToken
                      SessionPath = cfg.Telegram.SessionPath })
            |> ignore

            builder.Services.AddSingleton<ITelegramTransport>(fun sp ->
                sp.GetRequiredService<TelegramTransport>() :> ITelegramTransport)
            |> ignore

            builder.Services.AddSingleton<UpdateHandler>(fun sp ->
                let inbox = sp.GetRequiredService<ICommandInbox>()
                let users = sp.GetRequiredService<IUserRepository>()
                let logger = sp.GetRequiredService<ILogger<UpdateHandler>>()
                let dedupe = sp.GetRequiredService<UpdateDedupe>()

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
                        let! _ =
                            sp.GetRequiredService<IMessageOutbox>().Insert
                                env.CommandId
                                env.ChunkIndex
                                env.ChatId
                                env.RandomId
                                env.Payload

                        return ()
                    }

                UpdateHandler(whitelist, inbox, users, dedupe, admit, enqueueOutbox))
            |> ignore

            builder.Services.AddSingleton<OutboxDelivery>(fun sp ->
                OutboxDelivery(
                    sp.GetRequiredService<IMessageOutbox>(),
                    sp.GetRequiredService<ITelegramTransport>(),
                    sp.GetRequiredService<ILogger<OutboxDelivery>>()
                ))
            |> ignore

            builder.Services.AddHostedService<TelegramStartup>() |> ignore
            builder.Services.AddHostedService<OutboxDeliveryService>() |> ignore

            use host = builder.Build()

            try
                host.Run()
                0
            with ex ->
                eprintfn "phos: %s" ex.Message
                1
