namespace Phos.Speech

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Microsoft.Extensions.Logging
open SherpaOnnx

/// Speech-to-text service boundary. `TranscribeAsync` turns raw voice bytes
/// (Telegram document payload) into a transcript, returning typed errors.
type ISttService =
    abstract TranscribeAsync: byte[] -> CancellationToken -> Task<Result<SttResult, SttError>>
    abstract Validate: unit -> Result<unit, string>

/// Local speech-to-text service.
///
/// The recognizers are lazily created on first use and reused across calls
/// (model load is the expensive step). A `SemaphoreSlim` guards the lazy load,
/// and a second `SemaphoreSlim(MaxConcurrentStt)` serializes transcription so a
/// single CPU-bound recognizer is never shared concurrently.
///
/// Flow: size check → temp file → ffprobe (codec whitelist) → duration check →
/// ffmpeg decode → VAD (NoSpeech) → GigaAM → Whisper fallback on empty text.
type SttService(options: SttOptions, logger: ILogger<SttService>) =
    let loadLock = new SemaphoreSlim(1, 1)

    let transcribeLock =
        new SemaphoreSlim(options.MaxConcurrentStt, options.MaxConcurrentStt)

    let mutable recognizer: OfflineRecognizer option = None
    let mutable whisperRecognizer: OfflineRecognizer option = None
    let mutable disposed = false

    let sha256Of (path: string) : string =
        use fs = File.OpenRead path
        use sha = SHA256.Create()
        let hash = sha.ComputeHash fs
        BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()

    let requireFile (path: string) : Result<unit, string> =
        if File.Exists path then
            Ok()
        else
            Error(sprintf "STT model missing: %s — run scripts/provision-stt.sh" path)

    let getRecognizer () : OfflineRecognizer =
        match recognizer with
        | Some r -> r
        | None ->
            loadLock.Wait()

            try
                match recognizer with
                | Some r -> r
                | None ->
                    logger.LogInformation("loading GigaAM STT model")
                    let r = GigaAm.createRecognizer options
                    recognizer <- Some r
                    r
            finally
                loadLock.Release() |> ignore

    let getWhisperRecognizer () : OfflineRecognizer =
        match whisperRecognizer with
        | Some r -> r
        | None ->
            loadLock.Wait()

            try
                match whisperRecognizer with
                | Some r -> r
                | None ->
                    logger.LogInformation("loading Whisper STT fallback")
                    let r = Whisper.createRecognizer options
                    whisperRecognizer <- Some r
                    r
            finally
                loadLock.Release() |> ignore

    /// Deletes a temp file, ignoring any failure.
    let deleteIfExists (path: string) : unit =
        try
            if File.Exists path then
                File.Delete path
        with _ ->
            ()

    /// Extracts a short message from an `AudioDecodeError` for logging.
    let audioDecodeMessage (err: AudioDecodeError) : string =
        match err with
        | ProbeFailed msg -> msg
        | DecodeFailed msg -> msg

    /// Decodes and transcribes the already-written temp file. Assumes the
    /// caller has checked size and cancellation.
    let decodeAndTranscribe
        (inputPath: string)
        (wavPath: string)
        (ct: CancellationToken)
        : Task<Result<SttResult, SttError>> =
        task {
            match AudioDecode.probe options inputPath with
            | Error err ->
                let msg = audioDecodeMessage err
                logger.LogWarning("ffprobe failed: {Error}", msg)
                return Error(SttError.DecodeFailed msg)
            | Ok probe ->
                if ct.IsCancellationRequested then
                    return Error SttError.Cancelled
                elif not (AudioDecode.isSupportedCodec probe.Codec) then
                    return Error(SttError.UnsupportedCodec probe.Codec)
                elif probe.DurationSeconds > float options.MaxDurationSeconds then
                    return Error(SttError.TooLong(float options.MaxDurationSeconds, probe.DurationSeconds))
                else

                    match AudioDecode.decodeToWav options inputPath wavPath with
                    | Error err ->
                        let msg = audioDecodeMessage err
                        logger.LogWarning("ffmpeg decode failed: {Error}", msg)
                        return Error(SttError.DecodeFailed msg)
                    | Ok() ->

                        match AudioDecode.readWavSamples wavPath with
                        | Error e ->
                            logger.LogWarning("reading decoded WAV failed: {Error}", e)
                            return Error(SttError.DecodeFailed e)
                        | Ok(sr, samples) ->
                            if ct.IsCancellationRequested then
                                return Error SttError.Cancelled
                            else

                                let speech = SileroVad.detectSpeechSeconds options.VadModelPath samples sr

                                if speech <= 0.0 then
                                    return Error SttError.NoSpeech
                                elif ct.IsCancellationRequested then
                                    return Error SttError.Cancelled
                                else
                                    let gigaText = GigaAm.transcribe (getRecognizer ()) samples sr

                                    if ct.IsCancellationRequested then
                                        return Error SttError.Cancelled
                                    elif String.IsNullOrWhiteSpace gigaText && options.WhisperDir.IsSome then
                                        let whisperText = Whisper.transcribe (getWhisperRecognizer ()) samples sr

                                        return
                                            Ok
                                                { Text = whisperText
                                                  Engine = SttEngine.Whisper
                                                  AudioDurationSeconds = probe.DurationSeconds }
                                    else
                                        return
                                            Ok
                                                { Text = gigaText
                                                  Engine = SttEngine.GigaAm
                                                  AudioDurationSeconds = probe.DurationSeconds }
        }

    let transcribeCore (bytes: byte[]) (ct: CancellationToken) : Task<Result<SttResult, SttError>> =
        task {
            if bytes.LongLength > options.MaxBytes then
                return Error(SttError.TooLarge(options.MaxBytes, bytes.LongLength))
            elif ct.IsCancellationRequested then
                return Error SttError.Cancelled
            else
                let inputPath =
                    Path.Combine(Path.GetTempPath(), "phos-stt-in-" + Guid.NewGuid().ToString("N"))

                let wavPath =
                    Path.Combine(Path.GetTempPath(), "phos-stt-" + Guid.NewGuid().ToString("N") + ".wav")

                try
                    do! File.WriteAllBytesAsync(inputPath, bytes, ct)
                    return! decodeAndTranscribe inputPath wavPath ct
                finally
                    deleteIfExists inputPath
                    deleteIfExists wavPath
        }

    interface ISttService with
        member _.TranscribeAsync (bytes: byte[]) (ct: CancellationToken) : Task<Result<SttResult, SttError>> =
            task {
                try
                    do! transcribeLock.WaitAsync ct

                    try
                        return! transcribeCore bytes ct
                    finally
                        transcribeLock.Release() |> ignore
                with
                | :? OperationCanceledException -> return Error SttError.Cancelled
                | ex ->
                    logger.LogError(ex, "STT transcription failed")
                    return Error(SttError.ModelError ex.Message)
            }

        member _.Validate() : Result<unit, string> =
            result {
                do! requireFile options.ModelPath
                do! requireFile options.TokensPath
                do! requireFile options.VadModelPath

                match options.WhisperDir with
                | Some dir ->
                    do! requireFile (Path.Combine(dir, Whisper.encoderFile))
                    do! requireFile (Path.Combine(dir, Whisper.decoderFile))
                    do! requireFile (Path.Combine(dir, Whisper.tokensFile))
                | None -> ()

                let actual = sha256Of options.ModelPath

                if actual <> options.ModelSha256 then
                    return!
                        Error(
                            sprintf
                                "STT model checksum mismatch for %s: expected %s, got %s"
                                options.ModelPath
                                options.ModelSha256
                                actual
                        )
                else
                    return ()
            }

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                recognizer |> Option.iter (fun r -> r.Dispose())
                whisperRecognizer |> Option.iter (fun r -> r.Dispose())
                loadLock.Dispose()
                transcribeLock.Dispose()
