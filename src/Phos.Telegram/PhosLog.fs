namespace Phos.Telegram

open System
open Microsoft.Extensions.Logging
open Phos.Core.DomainTypes

/// High-performance logging helpers for Phos.Telegram.
///
/// F# has no partial methods, so the C# `[<LoggerMessage>]` source generator is
/// unavailable; the idiomatic high-performance F# path is the "legacy approach"
/// from https://learn.microsoft.com/dotnet/core/extensions/logging/high-performance-logging:
/// pre-defined `LoggerMessage.Define<T...>` delegates, whose message templates are
/// parsed ONCE at static initialization and whose arguments are boxed only when a
/// sink actually logs (no per-call `sprintf` allocation and no on-the-fly template
/// parsing). Each delegate is invoked as `(logger, args..., exception)` — the
/// `Exception` is ALWAYS the last `Action` parameter.
[<RequireQualifiedAccess>]
module PhosLog =

    /// Event id 1 — an outbox entry was sent.
    let sentOutbox: Action<ILogger, int64, int64, Exception | null> =
        LoggerMessage.Define<int64, int64>(
            LogLevel.Information,
            EventId(1, "SentOutbox"),
            "Outbox entry {OutboxId} sent (random_id {RandomId})"
        )

    /// Event id 2 — a flood/slowmode wait was hit; the entry is retried later.
    let floodWait: Action<ILogger, int, int64, int64, Exception | null> =
        LoggerMessage.Define<int, int64, int64>(
            LogLevel.Warning,
            EventId(2, "FloodWait"),
            "Flood/Slowmode wait {Seconds}s for outbox {OutboxId} (random_id {RandomId})"
        )

    /// Event id 3 — an outbox entry delivery failed.
    let deliveryFailed: Action<ILogger, int64, string, Exception | null> =
        LoggerMessage.Define<int64, string>(
            LogLevel.Warning,
            EventId(3, "DeliveryFailed"),
            "Outbox {OutboxId} delivery failed: {Error}"
        )

    /// Event id 4 — a peer could not be resolved for an outbox entry (post-
    /// restart, before hydration succeeds). Info level, once per delivery pass
    /// on the affected entry — never a repeated warning per heartbeat.
    let peerMissing: Action<ILogger, int64, ChatId, Exception | null> =
        LoggerMessage.Define<int64, ChatId>(
            LogLevel.Information,
            EventId(4, "PeerMissing"),
            "Outbox {OutboxId} deferred: peer for chat {ChatId} not resolvable yet"
        )
