namespace Phos.Speech

open SherpaOnnx

/// GigaAM v3 CTC int8 recognizer (primary engine). Mirrors the official
/// sherpa-onnx dotnet `offline-decode-files` example for `nemo_ctc` models.
module GigaAm =

    /// Feature dimension of the fbank features. Both 80 (the `from_nemo_ctc`
    /// default used by the benchmark) and 64 (the mel-bin count in the export
    /// script) produce identical, correct output on this model; 80 is used as
    /// it matches the verified benchmark configuration.
    let featureDim = 80

    /// Creates an `OfflineRecognizer` for the configured GigaAM model.
    let createRecognizer (options: SttOptions) : OfflineRecognizer =
        let mutable config = OfflineRecognizerConfig()
        config.FeatConfig.SampleRate <- 16000
        config.FeatConfig.FeatureDim <- featureDim
        config.ModelConfig.Tokens <- options.TokensPath
        config.ModelConfig.NeMoCtc.Model <- options.ModelPath
        config.ModelConfig.ModelType <- "nemo_ctc"
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
