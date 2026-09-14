module Phos.App.Program

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Phos.Core.Whitelist
open Phos.Storage
open Phos.Speech
open Phos.Telegram
open Phos.Omp
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

            let sttOptions = Config.toSttOptions cfg

            builder.Services.AddSingleton<ISttService>(fun sp ->
                new SttService(sttOptions, sp.GetRequiredService<ILogger<SttService>>()) :> ISttService)
            |> ignore

            builder.Services.AddSingleton<IVoiceProcessor>(fun sp ->
                VoiceProcessor(
                    sp.GetRequiredService<ITelegramTransport>(),
                    sp.GetRequiredService<ISttService>(),
                    sp.GetRequiredService<ILogger<VoiceProcessor>>()
                )
                :> IVoiceProcessor)
            |> ignore

            // --- OMP session manager wiring (Phase 5) -------------------------
            builder.Services.AddSingleton<WakeChannel>(fun _ -> WakeChannel()) |> ignore

            builder.Services.AddSingleton<ConcurrentDictionary<int64, CancellationTokenSource>>(fun _ ->
                ConcurrentDictionary<int64, CancellationTokenSource>())
            |> ignore

            let ompRoot = Config.expandHome "~/.omp"

            builder.Services.AddSingleton<ProfileManager>(fun _ -> ProfileManager ompRoot)
            |> ignore

            builder.Services.AddSingleton<WorkspaceManager>(fun _ ->
                let persona =
                    if String.IsNullOrWhiteSpace cfg.Omp.PersonaFile then
                        None
                    else
                        Some(Config.expandHome cfg.Omp.PersonaFile)

                WorkspaceManager(Config.expandHome cfg.Omp.WorkspaceRoot, ?personaFile = persona))
            |> ignore

            builder.Services.AddSingleton<OmpProcessOptions>(fun _ -> Config.toOmpProcessOptions cfg)
            |> ignore

            builder.Services.AddSingleton<HostToolExecutor>(fun sp ->
                HostToolExecutor(
                    sp.GetRequiredService<ITelegramTransport>(),
                    sp.GetRequiredService<IVoiceProcessor>(),
                    sp.GetRequiredService<ILogger<HostToolExecutor>>()
                ))
            |> ignore

            builder.Services.AddSingleton<HostUriResolver>(fun sp ->
                HostUriResolver(
                    sp.GetRequiredService<ITelegramTransport>(),
                    sp.GetRequiredService<ILogger<HostUriResolver>>()
                ))
            |> ignore

            builder.Services.AddSingleton<SessionManager>(fun sp ->
                let inbox = sp.GetRequiredService<ICommandInbox>()

                let heartbeats =
                    sp.GetRequiredService<ConcurrentDictionary<int64, CancellationTokenSource>>()

                let turnEnded (cmdId: int64) : Task<unit> =
                    task {
                        do! inbox.MarkCompleted cmdId

                        match heartbeats.TryRemove cmdId with
                        | true, cts -> cts.Cancel()
                        | _ -> ()
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

                SessionManager(
                    Config.toSessionManagerOptions cfg,
                    sp.GetRequiredService<ProfileManager>(),
                    sp.GetRequiredService<WorkspaceManager>(),
                    sp.GetRequiredService<OmpProcessOptions>(),
                    sp.GetRequiredService<HostToolExecutor>(),
                    sp.GetRequiredService<HostUriResolver>(),
                    enqueueOutbox,
                    turnEnded,
                    sp.GetRequiredService<ILogger<SessionManager>>()
                ))
            |> ignore

            if cfg.Omp.Enabled then
                builder.Services.AddHostedService<OmpWorker>(fun sp ->
                    new OmpWorker(
                        sp.GetRequiredService<ICommandInbox>(),
                        sp.GetRequiredService<IMessageOutbox>(),
                        sp.GetRequiredService<SessionManager>() :> IOmpSessionManager,
                        sp.GetRequiredService<WakeChannel>(),
                        sp.GetRequiredService<ConcurrentDictionary<int64, CancellationTokenSource>>(),
                        sp.GetRequiredService<ITelegramTransport>(),
                        sp.GetRequiredService<ILogger<OmpWorker>>()
                    ))
                |> ignore

            builder.Services.AddSingleton<UpdateHandler>(fun sp ->
                let inbox = sp.GetRequiredService<ICommandInbox>()
                let users = sp.GetRequiredService<IUserRepository>()
                let logger = sp.GetRequiredService<ILogger<UpdateHandler>>()
                let dedupe = sp.GetRequiredService<UpdateDedupe>()
                let wake = sp.GetRequiredService<WakeChannel>()

                let admit (env: CommandEnvelope) : Task<AdmitOutcome> =
                    task {
                        try
                            let! id = inbox.Insert env
                            wake.Wake()
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

                UpdateHandler(
                    whitelist,
                    inbox,
                    users,
                    dedupe,
                    admit,
                    enqueueOutbox,
                    sp.GetRequiredService<IVoiceProcessor>()
                ))
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

            builder.Services.AddHostedService<SttSelfTest>(fun sp ->
                SttSelfTest(
                    cfg.Stt,
                    sp.GetRequiredService<ISttService>(),
                    sp.GetRequiredService<ILogger<SttSelfTest>>()
                ))
            |> ignore

            // Never let an unhandled exception take the process down silently:
            // log it and keep running (or record it) so transient failures are
            // recoverable rather than fatal.
            AppDomain.CurrentDomain.UnhandledException.Add(fun args ->
                let detail =
                    if obj.ReferenceEquals(args.ExceptionObject, null) then
                        "unknown"
                    else
                        string args.ExceptionObject

                eprintfn "phos: unhandled exception: %s" detail)

            TaskScheduler.UnobservedTaskException.Add(fun args ->
                eprintfn "phos: unobserved task exception: %s" args.Exception.Message
                args.SetObserved())

            use host = builder.Build()

            try
                host.Run()
                0
            with ex ->
                eprintfn "phos: %s" (ex.ToString())
                1
