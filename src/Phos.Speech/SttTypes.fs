namespace Phos.Speech

open System
open System.IO

/// Configuration for the local speech-to-text service. Paths default to the
/// git-ignored spike data directory; every field is overridable through the
/// `Stt` config section.
type SttOptions =
    { ModelPath: string
      TokensPath: string
      VadModelPath: string
      WhisperDir: string option
      NumThreads: int
      MaxBytes: int64
      MaxDurationSeconds: int
      FfmpegPath: string
      FfmpegTimeoutSeconds: int
      ProbeTimeoutSeconds: int
      MaxConcurrentStt: int
      ModelSha256: string }

    /// Defaults follow the measured data on the target CPU (spike-03):
    /// `num_threads=4` gives p50 RTF 0.024 with low variance; the GigaAM v3
    /// CTC int8 model is pinned by sha256. Model dir is `PHOS_STT_MODEL_DIR`
    /// (default `spikes/spike-03-stt/data`).
    static member defaults: SttOptions =
        let dir =
            match Environment.GetEnvironmentVariable "PHOS_STT_MODEL_DIR" with
            | null -> "spikes/spike-03-stt/data"
            | "" -> "spikes/spike-03-stt/data"
            | d -> d

        { ModelPath = Path.Combine(dir, "model.int8.onnx")
          TokensPath = Path.Combine(dir, "tokens.txt")
          VadModelPath = Path.Combine(dir, "silero_vad.onnx")
          WhisperDir = None
          NumThreads = 4
          MaxBytes = 52_428_800L // 50 MiB
          MaxDurationSeconds = 300
          FfmpegPath = "ffmpeg"
          FfmpegTimeoutSeconds = 120
          ProbeTimeoutSeconds = 15
          MaxConcurrentStt = 1
          ModelSha256 = "f86ebfa0429ced91be6054fc344827e9c6c2572f3c318416cd974b06f66437ec" }

/// Engine that produced a transcript.
type SttEngine =
    | Auto
    | GigaAm
    | Whisper

/// Successful transcription result.
type SttResult =
    { Text: string
      Engine: SttEngine
      AudioDurationSeconds: float }

/// Typed speech-recognition failures, mapped to user-facing messages by the
/// caller. Expected errors never surface as exceptions.
type SttError =
    | TooLarge of maxBytes: int64 * actualBytes: int64
    | TooLong of maxSeconds: float * actualSeconds: float
    | UnsupportedCodec of string
    | DecodeFailed of string
    | NoSpeech
    | ModelError of string
    | Cancelled
