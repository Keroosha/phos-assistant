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

    /// Install the callback. Process-global static: call once at startup
    /// before any WTelegram.Client activity. Returns the previous delegate so
    /// tests can restore it.
    let wire (logger: ILogger) : Action<int, string> =
        let previous = Helpers.Log
        Helpers.Log <- fun level message -> logger.Log(levelOf level, message)
        previous
