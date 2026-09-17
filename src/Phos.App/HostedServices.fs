namespace Phos.App

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Phos.Omp
open Phos.Speech
open Phos.Storage
open Phos.Telegram

/// Durable finalization of a finished OMP turn. Success/abort complete the
/// command; a provider/model failure is finalized as `failed` (or
/// `dead_letter` at max attempts) WITHOUT the prompt-error auto-retry — the
/// command stays durably failed for manual/new-prompt recovery — and exactly
/// one generic, redacted Telegram error notice goes through the outbox.
module TurnFinalization =

    /// Generic user-facing text for an exhausted provider failure. Contains no
    /// provider details (HTML/messages/keys stay out of Telegram).
    let [<Literal>] ProviderFailureNotice =
        "⚠️ Модель не смогла завершить ответ. Попробуйте повторить запрос позже."

    /// Applies the turn outcome to the durable inbox and outbox. Idempotent:
    /// `MarkCompleted`/`MarkFailed`/`MarkNeedsReview` are status-guarded
    /// updates, so a repeated finalization of the same command is a no-op.
    let apply
        (inbox: ICommandInbox)
        (outbox: IMessageOutbox)
        (logger: ILogger)
        (cmdId: int64)
        (outcome: TurnOutcome)
        : Task<unit> =
        task {
            match outcome with
            | TurnOutcome.ProviderFailure reason ->
                logger.LogWarning("command {Id} failed with provider error: {Reason}", cmdId, reason)
                // MarkFailed without Retry: the command is NOT requeued.
                do! inbox.MarkFailed cmdId

                let! cmd = inbox.GetById cmdId

                match cmd with
                | Some c ->
                    let! _ =
                        outbox.Insert c.Id 0 c.Envelope.ChatId (Random.Shared.NextInt64()) ProviderFailureNotice []

                    ()
                | None -> ()
            | TurnOutcome.NeedsReview ->
                // Uncertain outcome (dead/wedged OMP, possibly after host-tool
                // activity): park durably for manual review, never auto-replay.
                logger.LogWarning("command {Id} parked for review: OMP runtime lost mid-turn", cmdId)
                do! inbox.MarkNeedsReview cmdId
            | TurnOutcome.Completed
            | TurnOutcome.Aborted -> do! inbox.MarkCompleted cmdId
        }

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
