namespace Phos.Telegram

open System
open Microsoft.Extensions.Logging
open WTelegram

/// Routes WTelegramClient's global log callback (WTelegram.Helpers.Log) into
/// our ILogger pipeline. The library's int severity IS the
/// Microsoft.Extensions.Logging.LogLevel enum (package README: "compatible
/// with the LogLevel enum"); its default logger writes colored lines straight
/// to Console — no timestamp, category, or level — bypassing our pipeline.
[<RequireQualifiedAccess>]
module WtLog =

    /// Map the library's int severity to LogLevel. Out-of-range values
    /// (0..6) degrade to Information instead of crashing.
    let levelOf (level: int) : LogLevel =
        if level >= int LogLevel.Trace && level <= int LogLevel.None then
            LanguagePrimitives.EnumOfValue<int, LogLevel> level
        else
            LogLevel.Information

    /// Downgrades the recoverable clock-resync notifications emitted by
    /// WTelegram plus the expected wrong-kind RPC responses from peer probes.
    /// WTelegram itself resets the message-id clock offset for BadMsg 16/17;
    /// this only prevents duplicate low-level Error logs.
    let levelFor (level: int) (message: string) : LogLevel =
        if
            message.Contains("RpcError 400 CHANNEL_INVALID", StringComparison.Ordinal)
            || message.Contains("RpcError 400 CHAT_ID_INVALID", StringComparison.Ordinal)
            || message.Contains("BadMsgNotification 16", StringComparison.Ordinal)
            || message.Contains("BadMsgNotification 17", StringComparison.Ordinal)
        then
            LogLevel.Debug
        else
            levelOf level


    /// Install the callback. Process-global static: call once at startup
    /// before any WTelegram.Client activity. Returns the previous delegate so
    /// tests can restore it.
    let wire (logger: ILogger) : Action<int, string> =
        let previous = Helpers.Log
        Helpers.Log <- fun level message -> logger.Log(levelFor level message, message)
        previous
