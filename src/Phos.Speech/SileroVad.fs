namespace Phos.Speech

open SherpaOnnx

/// Silence detection via the sherpa-onnx Silero VAD model. The VAD is run
/// over the decoded 16 kHz mono PCM stream; the returned speech-seconds value
/// is the total length of the detected speech segments. A value of `0.0` means
/// no speech was found.
module SileroVad =

    /// Returns the total seconds of detected speech in `samples`.
    let detectSpeechSeconds (vadModelPath: string) (samples: float32[]) (sampleRate: int) : float =
        if Array.isEmpty samples then
            0.0
        else
            let mutable vadConfig = VadModelConfig()
            vadConfig.SileroVad.Model <- vadModelPath
            vadConfig.SileroVad.Threshold <- 0.5f
            vadConfig.SileroVad.MinSilenceDuration <- 0.5f
            vadConfig.SileroVad.MinSpeechDuration <- 0.25f
            vadConfig.SileroVad.MaxSpeechDuration <- 5.0f
            vadConfig.SileroVad.WindowSize <- 512
            vadConfig.SampleRate <- sampleRate
            vadConfig.Debug <- 0

            use vad = new VoiceActivityDetector(vadConfig, 60.0f)
            let windowSize = 512
            let numIter = samples.Length / windowSize
            let mutable totalSamples = 0.0

            for i in 0 .. numIter - 1 do
                let start = i * windowSize
                let chunk = Array.zeroCreate<float32> windowSize
                System.Array.Copy(samples, start, chunk, 0, windowSize)
                vad.AcceptWaveform chunk

                if vad.IsSpeechDetected() then
                    while not (vad.IsEmpty()) do
                        let seg = vad.Front()
                        totalSamples <- totalSamples + float seg.Samples.Length
                        vad.Pop()

            vad.Flush()

            while not (vad.IsEmpty()) do
                let seg = vad.Front()
                totalSamples <- totalSamples + float seg.Samples.Length
                vad.Pop()

            totalSamples / float sampleRate

    /// True when the decoded audio contains any speech.
    let hasSpeech (vadModelPath: string) (samples: float32[]) (sampleRate: int) : bool =
        detectSpeechSeconds vadModelPath samples sampleRate > 0.0
