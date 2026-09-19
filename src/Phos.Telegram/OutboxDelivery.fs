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
/// the SAME `random_id` (never a new message). A `MissingPeer` caused by login
/// readiness or the hydration cooldown is deferred without consuming an
/// attempt; a completed failed probe consumes one bounded retry attempt. Any
/// other error is marked failed. Before sending, `GetByRandomId` guards against
/// re-sending a `random_id` that is already sent/sending.
///
/// Deferred rows carry a not-before timestamp, so later chats use the same
/// delivery loop instead of waiting behind an unresolved peer.
type OutboxDelivery
    (outbox: IMessageOutbox, transport: ITelegramTransport, logger: ILogger, ?missingPeerBackoff: TimeSpan) =
    /// Backoff after a pass ended on a missing-peer deferral. Keeps the loop
    /// from spinning on a chat whose peer cannot be resolved (e.g. the bot has
    /// never interacted with it).
    let missingPeerBackoff = defaultArg missingPeerBackoff (TimeSpan.FromSeconds 30.0)

    /// Delivers at most one pending outbox entry. Returns `true` if an entry was
    /// processed (sent, marked failed, or scheduled for a flood-wait retry).
    member _.DeliverOnceAsync(ct: CancellationToken) : Task<bool> =
        task {
            let! entry = outbox.NextPending()

            match entry with
            | None -> return false
            | Some entry ->
                // NextPending returns pending/failed entries and stale sending
                // entries left by a crashed process; BeginSend reclaims only
                // rows outside the same freshness window.
                do! outbox.BeginSend entry.Id

                match entry.Media with
                | Some media ->
                    let target =
                        { ChatId = entry.ChatId
                          RandomId = entry.RandomId
                          Caption = entry.Payload
                          Entities = entry.Entities |> List.map EntitySend.toTelegramEntity
                          Media = media }

                    match! transport.SendMedia target with
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
                    | Error(MissingPeer(chatId, reason)) ->
                        // A restart empties the in-memory peer cache while outbox
                        // rows survive. A login/cooldown miss is deferred without
                        // consuming an attempt; a completed failed probe consumes
                        // one bounded attempt before the same not-before delay.
                        match reason with
                        | MissingPeerReason.LoginNotReady ->
                            do! outbox.Defer entry.Id (DateTimeOffset.UtcNow.Add missingPeerBackoff)
                        | MissingPeerReason.HydrationFailed ->
                            do! outbox.MarkFailed entry.Id
                            do! outbox.Defer entry.Id (DateTimeOffset.UtcNow.Add missingPeerBackoff)

                        PhosLog.peerMissing.Invoke(logger, entry.Id, chatId, null)
                        return true
                    | Error(Other msg) ->
                        do! outbox.MarkFailed entry.Id
                        PhosLog.deliveryFailed.Invoke(logger, entry.Id, msg, null)
                        return true
                | None ->
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
                    | Error(MissingPeer(chatId, reason)) ->
                        // A restart empties the in-memory peer cache while outbox
                        // rows survive. A login/cooldown miss is deferred without
                        // consuming an attempt; a completed failed probe consumes
                        // one bounded attempt before the same not-before delay.
                        match reason with
                        | MissingPeerReason.LoginNotReady ->
                            do! outbox.Defer entry.Id (DateTimeOffset.UtcNow.Add missingPeerBackoff)
                        | MissingPeerReason.HydrationFailed ->
                            do! outbox.MarkFailed entry.Id
                            do! outbox.Defer entry.Id (DateTimeOffset.UtcNow.Add missingPeerBackoff)

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
