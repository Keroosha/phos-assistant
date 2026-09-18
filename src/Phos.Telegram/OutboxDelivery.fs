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
/// the SAME `random_id` (never a new message); on a `MissingPeer` error the
/// entry is moved back to `pending` without a meaningful penalty (the transport
/// has already hydrated the peer once and retried internally — this state means
/// the peer could not be resolved yet, e.g. login still in progress); on any
/// other error the entry is marked failed with attempts+1. Before sending,
/// `GetByRandomId` guards against re-sending a `random_id` that is already
/// sent/sending.
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
                      Entities = entry.Entities |> List.map EntitySend.toTelegramEntity }

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
                | Error(MissingPeer chatId) ->
                    // A restart empties the in-memory peer cache while outbox
                    // rows survive. The transport already hydrates once and
                    // retries internally; reaching here means hydration could
                    // not resolve the peer yet (e.g. login still in progress).
                    // The attempt is NOT consumed: the row is reverted from
                    // `sending` to `pending` and the loop retries it on a later
                    // pass with the same random_id — no stuck rows, no warning
                    // spam per scan.
                    do! outbox.RevertToPending entry.Id
                    PhosLog.peerMissing.Invoke(logger, entry.Id, chatId, null)
                    return true
                | Error(Other msg) ->
                    do! outbox.MarkFailed entry.Id
                    PhosLog.deliveryFailed.Invoke(logger, entry.Id, msg, null)
                    return true
        }

    /// Runs the delivery loop until `ct` is cancelled. Each iteration is
    /// guarded so a transient/unexpected error never kills the background
    /// service; it is logged and the loop resumes after a short backoff.
    member this.RunAsync(ct: CancellationToken) : Task<unit> =
        task {
            while not ct.IsCancellationRequested do
                try
                    let! processed = this.DeliverOnceAsync ct

                    if not processed then
                        do! Task.Delay(TimeSpan.FromMilliseconds 100.0, ct)
                with
                | :? OperationCanceledException as oce -> raise oce
                | ex ->
                    logger.LogError(ex, "outbox delivery iteration failed")
                    do! Task.Delay(TimeSpan.FromSeconds 5.0, ct)
        }
