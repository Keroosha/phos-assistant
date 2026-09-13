namespace Phos.Telegram

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Phos.Speech

/// Voice-processing boundary so the update handler can be tested without a real
/// STT service or Telegram transport.
type IVoiceProcessor =
    abstract ProcessAsync: VoiceRef -> Task<Result<string, string>>

/// Downloads a voice message via the transport and transcribes it through the
/// local STT service. Typed `SttError`s are mapped to user-facing Russian
/// messages; only I/O boundaries surface as `Result` errors, never exceptions.
type VoiceProcessor(transport: ITelegramTransport, stt: ISttService, logger: ILogger<VoiceProcessor>) =

    /// Maps a typed STT failure to a user-facing Russian message.
    let toUserMessage (err: SttError) : string =
        match err with
        | TooLarge _ -> "голосовое слишком большое"
        | TooLong _ -> "голосовое слишком длинное"
        | DecodeFailed _
        | UnsupportedCodec _ -> "не удалось декодировать аудио"
        | NoSpeech -> "не удалось распознать речь"
        | ModelError _
        | Cancelled -> "ошибка распознавания"

    interface IVoiceProcessor with
        member _.ProcessAsync(voice: VoiceRef) : Task<Result<string, string>> =
            task {
                let! bytes = transport.DownloadVoice voice

                if bytes.Length = 0 then
                    logger.LogWarning("voice download returned empty bytes for {MessageId}", voice.MessageId)
                    return Error "empty voice download"
                else

                    match! stt.TranscribeAsync bytes System.Threading.CancellationToken.None with
                    | Ok result -> return Ok result.Text
                    | Error err ->
                        logger.LogWarning("voice transcription failed for {MessageId}: {Error}", voice.MessageId, err)
                        return Error(toUserMessage err)
            }
