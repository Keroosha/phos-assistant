namespace Phos.Speech

open System
open System.IO
open SherpaOnnx

/// Whisper small (multilingual, int8) recognizer — the explicit fallback engine
/// used when GigaAM returns an empty transcript.
module Whisper =

    /// File names expected inside `SttOptions.WhisperDir`.
    let encoderFile = "small-encoder.int8.onnx"
    let decoderFile = "small-decoder.int8.onnx"
    let tokensFile = "small-tokens.txt"

    /// Creates an `OfflineRecognizer` for the Whisper small model in
    /// `options.WhisperDir`. Fails (throws) if the directory is not configured;
    /// the caller only invokes this when `WhisperDir` is `Some`.
    let createRecognizer (options: SttOptions) : OfflineRecognizer =
        let dir =
            match options.WhisperDir with
            | Some d -> d
            | None -> invalidArg "options.WhisperDir" "whisper fallback requires a configured WhisperDir"

        let mutable config = OfflineRecognizerConfig()
        config.FeatConfig.SampleRate <- 16000
        config.FeatConfig.FeatureDim <- 80
        config.ModelConfig.Tokens <- Path.Combine(dir, tokensFile)
        config.ModelConfig.Whisper.Encoder <- Path.Combine(dir, encoderFile)
        config.ModelConfig.Whisper.Decoder <- Path.Combine(dir, decoderFile)
        config.ModelConfig.Whisper.Language <- "ru"
        config.ModelConfig.Whisper.Task <- "transcribe"
        config.ModelConfig.ModelType <- "whisper"
        config.ModelConfig.NumThreads <- options.NumThreads
        config.ModelConfig.Debug <- 0
        config.DecodingMethod <- "greedy_search"

        new OfflineRecognizer(config)

    /// Transcribes `samples` (16 kHz mono) and returns the recognized text.
    let transcribe (recognizer: OfflineRecognizer) (samples: float32[]) (sampleRate: int) : string =
        let stream = recognizer.CreateStream()
        stream.AcceptWaveform(sampleRate, samples)
        recognizer.Decode(stream)
        stream.Result.Text
