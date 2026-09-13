namespace Phos.App

open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Phos.Telegram

/// Hosted service that wires the Telegram update handler and performs the bot
/// login as part of host startup. Registering via `AddHostedService` means a
/// login failure (or any startup exception) surfaces through `host.Run()`.
type TelegramStartup(transport: TelegramTransport, handler: UpdateHandler, logger: ILogger<TelegramStartup>) =
    interface IHostedService with
        member _.StartAsync(ct: CancellationToken) : Task =
            task {
                transport.OnUpdate(fun updates ->
                    task {
                        for u in UpdateModel.mapUpdates updates do
                            try
                                let! _ = handler.HandleAsync u
                                ()
                            with ex ->
                                logger.LogError(ex, "handle update failed")
                    })

                let! bot = (transport :> ITelegramTransport).Login()
                logger.LogInformation("phos ready, bot id {BotId}", bot.BotId)
            }
            :> Task

        member _.StopAsync(ct: CancellationToken) : Task = Task.CompletedTask

/// Background service that runs the transactional outbox delivery loop for the
/// lifetime of the host. Cancellation of the host's stopping token tears the
/// loop down.
type OutboxDeliveryService(delivery: OutboxDelivery) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) : Task = delivery.RunAsync stoppingToken :> Task
