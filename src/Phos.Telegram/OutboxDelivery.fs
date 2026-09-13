namespace Phos.Telegram

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Phos.Storage

/// Delivers messages from the transactional outbox via the transport.
///
/// Each entry carries a stable MTProto `random_id`. On success the entry is
/// marked sent; on `FLOOD_WAIT`/`SLOWMODE_WAIT` the entry is retried later with
/// the SAME `random_id` (never a new message); on any other error the entry is
/// marked failed with attempts+1. Before sending, `GetByRandomId` guards against
/// re-sending a `random_id` that is already sent/sending.
type OutboxDelivery(outbox: IMessageOutbox, transport: ITelegramTransport, logger: ILogger) =
    /// Delivers at most one pending outbox entry. Returns `true` if an entry was
    /// processed (sent, marked failed, or scheduled for a flood-wait retry).
    member _.DeliverOnceAsync(ct: CancellationToken) : Task<bool> =
        task {
            let! entry = outbox.NextPending()

            match entry with
            | None -> return false
            | Some entry ->
                // NextPending only ever returns retryable entries ('pending' or
                // retryable 'failed'), so no idempotency re-check is needed here;
                // the stable random_id is preserved across retries by the outbox.
                do! outbox.BeginSend entry.Id

                let target =
                    { ChatId = entry.ChatId
                      RandomId = entry.RandomId
                      Text = entry.Payload
                      Entities = [] }

                match! transport.SendMessage target with
                | Ok result ->
                    do! outbox.MarkSent entry.Id result.RemoteMessageId
                    PhosLog.sentOutbox.Invoke(logger, entry.Id, entry.RandomId, null)
                    return true
                | Error(FloodWait seconds | SlowModeWait seconds) ->
                    // Move out of 'sending' back to a retryable state;
                    // the random_id is never changed, so a retry does
                    // not create a second message.
                    do! outbox.MarkFailed entry.Id
                    do! outbox.Retry entry.Id
                    PhosLog.floodWait.Invoke(logger, seconds, entry.Id, entry.RandomId, null)
                    do! Task.Delay(TimeSpan.FromSeconds(float seconds), ct)
                    return true
                | Error(Other msg) ->
                    do! outbox.MarkFailed entry.Id
                    PhosLog.deliveryFailed.Invoke(logger, entry.Id, msg, null)
                    return true
        }

    /// Runs the delivery loop until `ct` is cancelled.
    member this.RunAsync(ct: CancellationToken) : Task<unit> =
        task {
            while not ct.IsCancellationRequested do
                let! processed = this.DeliverOnceAsync ct

                if not processed then
                    do! Task.Delay(TimeSpan.FromMilliseconds 100.0, ct)
        }
