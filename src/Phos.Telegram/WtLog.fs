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
    /// Downgrades the two expected wrong-kind RPC responses produced while
    /// probing a raw ChatId as user/channel/basic-group. The transport still
    /// handles the exception; this only removes duplicate WTelegram error logs.
    let levelFor (level: int) (message: string) : LogLevel =
        if
            message.Contains("RpcError 400 CHANNEL_INVALID", StringComparison.Ordinal)
            || message.Contains("RpcError 400 CHAT_ID_INVALID", StringComparison.Ordinal)
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
