namespace Phos.App

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Phos.Speech
open Phos.Telegram

/// Hosted service that wires the Telegram update handler and performs the bot
/// login. Login NEVER fails host startup: a transient failure (e.g. 420
/// FLOOD_WAIT) is retried in the background with a flood-aware backoff, so a
/// single bad auth attempt cannot take the process down.
type TelegramStartup(transport: TelegramTransport, handler: UpdateHandler, logger: ILogger<TelegramStartup>) =
    let loginCts = new CancellationTokenSource()

    interface IHostedService with
        member _.StartAsync(_: CancellationToken) : Task =
            task {
                transport.OnUpdate(fun updates ->
                    task {
                        try
                            for u in UpdateModel.mapUpdates updates do
                                try
                                    let! _ = handler.HandleAsync u
                                    ()
                                with ex ->
                                    logger.LogError(ex, "handle update failed")
                        with ex ->
                            logger.LogError(ex, "map updates failed")
                    })

                let loginLoop () : Task =
                    task {
                        let mutable ready = false

                        while not ready && not loginCts.Token.IsCancellationRequested do
                            try
                                let! bot = (transport :> ITelegramTransport).Login()
                                logger.LogInformation("phos ready, bot id {BotId}", bot.BotId)
                                ready <- true
                            with ex ->
                                let delay = Transport.loginRetryDelay ex

                                logger.LogWarning(
                                    ex,
                                    "bot login failed, retrying in {DelaySeconds}s",
                                    delay.TotalSeconds
                                )

                                try
                                    do! Task.Delay(delay, loginCts.Token)
                                with :? OperationCanceledException ->
                                    ()
                    }

                Task.Run(Func<Task>(fun () -> loginLoop ())) |> ignore
            }
            :> Task

        member _.StopAsync(_: CancellationToken) : Task = task { loginCts.Cancel() } :> Task

/// Background service that runs the transactional outbox delivery loop for the
/// lifetime of the host. Cancellation of the host's stopping token tears the
/// loop down.
type OutboxDeliveryService(delivery: OutboxDelivery) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) : Task = delivery.RunAsync stoppingToken :> Task

/// Hosted service that validates the STT model files (existence + checksum pin)
/// shortly after startup. A failure is logged but does NOT crash the host — the
/// resilience rule — so a transient provisioning gap never takes the process
/// down; subsequent per-call transcriptions surface a `ModelError`.
type SttSelfTest(settings: SttSettings, stt: ISttService, logger: ILogger<SttSelfTest>) =
    interface IHostedService with
        member _.StartAsync(_: CancellationToken) : Task =
            task {
                if settings.Enabled then
                    match stt.Validate() with
                    | Ok() -> logger.LogInformation("STT models validated")
                    | Error msg -> logger.LogError("STT validation failed: {Message}", msg)
            }
            :> Task

        member _.StopAsync(_: CancellationToken) : Task = Task.CompletedTask
