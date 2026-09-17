namespace Phos.Omp

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Phos.Storage

/// Durable finalization of a finished OMP turn. Success/abort complete the
/// command; a provider/model failure is finalized as `failed` (or
/// `dead_letter` at max attempts) WITHOUT the prompt-error auto-retry — the
/// command stays durably failed for manual/new-prompt recovery — and exactly
/// one generic, redacted Telegram error notice goes through the outbox.
module TurnFinalization =

    /// Generic user-facing text for an exhausted provider failure. Contains no
    /// provider details (HTML/messages/keys stay out of Telegram).
    [<Literal>]
    let ProviderFailureNotice =
        "⚠️ Модель не смогла завершить ответ. Попробуйте повторить запрос позже."

    /// Generic user-facing text for an uncertain process/turn outcome. It
    /// explicitly says that Phos did not replay the request automatically.
    [<Literal>]
    let NeedsReviewNotice =
        "⚠️ Обработка прервана из-за сбоя. Запрос не был повторён автоматически."

    /// Applies the turn outcome to the durable inbox and outbox. Provider
    /// finalization is idempotent: a duplicate callback must not increment the
    /// inbox attempt count again or turn a completed command back into failed.
    let apply
        (inbox: ICommandInbox)
        (outbox: IMessageOutbox)
        (logger: ILogger)
        (cmdId: int64)
        (outcome: TurnOutcome)
        : Task<unit> =
        task {
            let enqueueNotice (payload: string) (c: Command) : Task<unit> =
                task {
                    let! _ = outbox.Insert c.Id 0 c.Envelope.ChatId (Random.Shared.NextInt64()) payload []
                    return ()
                }

            match outcome with
            | TurnOutcome.ProviderFailure reason ->
                logger.LogWarning("command {Id} failed with provider error: {Reason}", cmdId, reason)

                // Check the current status before changing it. The storage
                // transition is also guarded, so duplicate callbacks cannot
                // increment attempts or revive a completed command.
                let! current = inbox.GetById cmdId

                match current with
                | Some c when
                    c.Status = Phos.Core.InboxStateMachine.Status.Claimed
                    || c.Status = Phos.Core.InboxStateMachine.Status.Running
                    ->
                    do! inbox.MarkFailed cmdId
                    let! failed = inbox.GetById cmdId

                    match failed with
                    | Some c -> do! enqueueNotice ProviderFailureNotice c
                    | None -> ()
                | Some c when
                    c.Status = Phos.Core.InboxStateMachine.Status.Failed
                    || c.Status = Phos.Core.InboxStateMachine.Status.DeadLetter
                    ->
                    // A prior attempt may have persisted the status before
                    // failing to enqueue the notice. The outbox uniqueness
                    // constraint makes this safe to repair/replay.
                    do! enqueueNotice ProviderFailureNotice c
                | _ -> ()
            | TurnOutcome.NeedsReview ->
                // Uncertain outcome (dead/wedged OMP, possibly after host-tool
                // activity): park durably for manual review, never auto-replay.
                logger.LogWarning("command {Id} parked for review: OMP runtime lost mid-turn", cmdId)
                do! inbox.MarkNeedsReview cmdId

                let! parked = inbox.GetById cmdId

                match parked with
                | Some c when c.Status = Phos.Core.InboxStateMachine.Status.NeedsReview ->
                    // Use the same command/chunk uniqueness boundary as the
                    // provider error notice. A duplicate callback is harmless,
                    // and a late normal answer cannot replace the review notice.
                    do! enqueueNotice NeedsReviewNotice c
                | _ -> ()
            | TurnOutcome.Completed
            | TurnOutcome.Aborted -> do! inbox.MarkCompleted cmdId
        }
