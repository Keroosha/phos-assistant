module Phos.Tests.SpeechTests

// FS3511: awaiting `Task<Result<...>>` then pattern-matching in the `task` CE
// cannot be statically compiled; F# falls back to a dynamic state machine. This
// is a compiler performance note, not a correctness issue — the tests run
// correctly. Escalated to an error by TreatWarningsAsErrors, so suppress here.
#nowarn "3511"

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open Microsoft.Extensions.Logging.Abstractions
open Phos.Speech

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Locates the git-ignored spike data directory by walking up from the current
/// directory until `spikes/spike-03-stt/data` is found (the `dotnet test` host
/// cwd is the test output dir, not the repo root).
[<TailCall>]
let rec private findDataDir (dir: string) : string option =
    let candidate = Path.Combine(dir, "spikes", "spike-03-stt", "data")

    if Directory.Exists candidate then
        Some candidate
    else
        match Option.ofObj (Path.GetDirectoryName dir) with
        | Some parent when parent <> dir -> findDataDir parent
        | _ -> None

let private dataDir: string =
    match findDataDir (Directory.GetCurrentDirectory()) with
    | Some d -> d
    | None -> Path.Combine("spikes", "spike-03-stt", "data")

let private modelPath = Path.Combine(dataDir, "model.int8.onnx")
let private tokensPath = Path.Combine(dataDir, "tokens.txt")
let private vadModelPath = Path.Combine(dataDir, "silero_vad.onnx")
let private whisperDir = Path.Combine(dataDir, "whisper")
let private ruExampleWav = Path.Combine(dataDir, "ru_example.wav")

/// The canonical reference transcript for the WER fixture (Pushkin, «Борис
/// Годунов» — the monastery scene). The model output differs by one word
/// (`убогий` vs `убогой`), giving a measured WER of ~0.045.
let private canonicalReference =
    "но в кельях тихо и темно уже и сам игумен строгий свои молитвы прекратил и кости ветхие склонил перекрестясь на одр убогой"

/// Fails the test with the standard message when a model file is missing.
let private requireModel (path: string) : unit =
    if not (File.Exists path) then
        failwith (sprintf "STT model missing: %s — run scripts/provision-stt.sh" path)

/// Base options pointing at the located data directory.
let private baseOptions: SttOptions =
    { SttOptions.defaults with
        ModelPath = modelPath
        TokensPath = tokensPath
        VadModelPath = vadModelPath }

/// Writes a mono PCM16 WAV to `path`.
let private writeWav (path: string) (sampleRate: int) (samples: float[]) : unit =
    use bw = new BinaryWriter(File.Create path)
    let dataSize = samples.Length * 2

    bw.Write [| 0x52uy; 0x49uy; 0x46uy; 0x46uy |] // "RIFF"
    bw.Write(36 + dataSize)
    bw.Write [| 0x57uy; 0x41uy; 0x56uy; 0x45uy |] // "WAVE"
    bw.Write [| 0x66uy; 0x6duy; 0x74uy; 0x20uy |] // "fmt "
    bw.Write 16 // fmt chunk size
    bw.Write(int16 1) // PCM
    bw.Write(int16 1) // mono
    bw.Write sampleRate
    bw.Write(sampleRate * 2) // byte rate
    bw.Write(int16 2) // block align
    bw.Write(int16 16) // bits per sample
    bw.Write [| 0x64uy; 0x61uy; 0x74uy; 0x61uy |] // "data"
    bw.Write dataSize

    for s in samples do
        let v = int16 (Math.Clamp(s, -1.0, 1.0) * 32767.0)
        bw.Write v

/// Word-level Levenshtein distance between two strings.
let private wordLevenshtein (a: string[]) (b: string[]) : int =
    let n = a.Length
    let m = b.Length
    let dp = Array2D.create (n + 1) (m + 1) 0

    for i in 0..n do
        dp.[i, 0] <- i

    for j in 0..m do
        dp.[0, j] <- j

    for i in 1..n do
        for j in 1..m do
            let cost = if a.[i - 1] = b.[j - 1] then 0 else 1

            dp.[i, j] <- min (dp.[i - 1, j] + 1) (min (dp.[i, j - 1] + 1) (dp.[i - 1, j - 1] + cost))

    dp.[n, m]

/// Word Error Rate between `reference` and `hypothesis`.
let private wer (reference: string) (hypothesis: string) : float =
    let split (s: string) =
        s.Split([| ' '; '\t'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)

    let refWords = split reference
    let hypWords = split hypothesis

    if refWords.Length = 0 then
        0.0
    else
        float (wordLevenshtein refWords hypWords) / float refWords.Length

/// Single lazily-created GigaAM STT service, shared by the model-dependent
/// tests (the first transcription loads the model and is slow ~2-5s).
let private gigaService =
    lazy (new SttService(baseOptions, NullLogger<SttService>.Instance))

/// Single lazily-created SttService with the Whisper fallback enabled.
let private whisperService =
    lazy
        (new SttService(
            { baseOptions with
                WhisperDir = Some whisperDir },
            NullLogger<SttService>.Instance
        ))

/// Reads the fixture WAV bytes for the STT pipeline.
let private readFixtureBytes (path: string) : byte[] = File.ReadAllBytes path

/// Creates a unique temp directory, runs `body`, then always deletes the
/// directory on success. Awaiting is done outside a `try` block so the task
/// state machine stays statically compilable (a `let!` inside `try` triggers
/// FS3511, escalated to an error by TreatWarningsAsErrors).
let private withTempDir (body: string -> Task<'T>) : Task<'T> =
    let dir =
        Path.Combine(Path.GetTempPath(), "phos-stt-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore

    task {
        let! result = body dir

        try
            Directory.Delete(dir, true)
        with _ ->
            ()

        return result
    }

// ---------------------------------------------------------------------------
// Non-model tests (run without requiring the model files)
// ---------------------------------------------------------------------------

[<Fact>]
let ``defaults follow the measured benchmark data`` () =
    SttOptions.defaults.NumThreads |> should equal 4
    SttOptions.defaults.MaxDurationSeconds |> should equal 300
    SttOptions.defaults.MaxConcurrentStt |> should equal 1
    SttOptions.defaults.MaxBytes |> should equal 52_428_800L

[<Fact>]
let ``defaults respect the PHOS_STT_MODEL_DIR environment variable`` () =
    let original = Environment.GetEnvironmentVariable "PHOS_STT_MODEL_DIR"

    try
        Environment.SetEnvironmentVariable("PHOS_STT_MODEL_DIR", "/tmp/stt-models")

        SttOptions.defaults.ModelPath
        |> should equal (Path.Combine("/tmp/stt-models", "model.int8.onnx"))
    finally
        Environment.SetEnvironmentVariable("PHOS_STT_MODEL_DIR", original)

[<Fact>]
let ``supported codec whitelist accepts common voice codecs`` () =
    AudioDecode.isSupportedCodec "opus" |> should equal true
    AudioDecode.isSupportedCodec "ogg" |> should equal true
    AudioDecode.isSupportedCodec "vorbis" |> should equal true
    AudioDecode.isSupportedCodec "mp3" |> should equal true
    AudioDecode.isSupportedCodec "aac" |> should equal true
    AudioDecode.isSupportedCodec "wav" |> should equal true
    AudioDecode.isSupportedCodec "pcm_s16le" |> should equal true
    AudioDecode.isSupportedCodec "pcm_s24le" |> should equal true
    AudioDecode.isSupportedCodec "pcm_f32le" |> should equal true
    AudioDecode.isSupportedCodec "OPUS" |> should equal true
    AudioDecode.isSupportedCodec "video/mp4" |> should equal false
    AudioDecode.isSupportedCodec "flac" |> should equal false

[<Fact>]
let ``parseProbeJson reads duration from a number or a string`` () =
    let jsonNum =
        """{"format":{"duration":12.5},"streams":[{"codec_type":"audio","codec_name":"opus"}]}"""

    match AudioDecode.parseProbeJson jsonNum with
    | Ok info ->
        info.DurationSeconds |> should equal 12.5
        info.Codec |> should equal "opus"
    | Error e -> failwith e

    let jsonStr =
        """{"format":{"duration":"7.25"},"streams":[{"codec_type":"audio","codec_name":"mp3"}]}"""

    match AudioDecode.parseProbeJson jsonStr with
    | Ok info ->
        info.DurationSeconds |> should equal 7.25
        info.Codec |> should equal "mp3"
    | Error e -> failwith e

[<Fact>]
let ``parseProbeJson rejects a stream-less probe`` () =
    let json = """{"format":{"duration":1.0},"streams":[]}"""

    match AudioDecode.parseProbeJson json with
    | Error _ -> ()
    | Ok _ -> failwith "expected an error for a probe with no audio stream"

[<Fact>]
let ``parseProbeJson handles missing fields and malformed JSON`` () =
    // No "format" section: duration defaults to 0 but a stream is still required.
    match AudioDecode.parseProbeJson """{"streams":[{"codec_type":"audio","codec_name":"aac"}]}""" with
    | Ok info ->
        info.DurationSeconds |> should equal 0.0
        info.Codec |> should equal "aac"
    | Error e -> failwith e

    // No audio stream (only a video stream) → error.
    match
        AudioDecode.parseProbeJson
            """{"format":{"duration":1.0},"streams":[{"codec_type":"video","codec_name":"h264"}]}"""
    with
    | Error _ -> ()
    | Ok _ -> failwith "expected an error for a probe that has no audio stream"

    // Malformed JSON → error.
    match AudioDecode.parseProbeJson "{ not json" with
    | Error _ -> ()
    | Ok _ -> failwith "expected an error for malformed JSON"

[<Fact>]
let ``buildFfmpegArgs decodes to mono 16 kHz PCM s16le`` () =
    let args = AudioDecode.buildFfmpegArgs "/tmp/out.wav" "/tmp/in.ogg"

    args
    |> should
        equal
        [ "-y"
          "-loglevel"
          "error"
          "-i"
          "/tmp/in.ogg"
          "-ar"
          "16000"
          "-ac"
          "1"
          "-c:a"
          "pcm_s16le"
          "/tmp/out.wav" ]

[<Fact>]
let ``readWavSamples rejects a non-WAV payload`` () =
    withTempDir (fun dir ->
        task {
            let bad = Path.Combine(dir, "bad.wav")
            File.WriteAllBytes(bad, [| 0x00uy; 0x01uy; 0x02uy; 0x03uy |])

            match AudioDecode.readWavSamples bad with
            | Error _ -> ()
            | Ok _ -> failwith "expected an error for a non-WAV payload"

            let wave = Path.Combine(dir, "wave.wav")
            // RIFF + size but the next four bytes are not "WAVE".
            File.WriteAllBytes(
                wave,
                [| 0x52uy
                   0x49uy
                   0x46uy
                   0x46uy
                   0x10uy
                   0x00uy
                   0x00uy
                   0x00uy
                   0x00uy
                   0x00uy
                   0x00uy
                   0x00uy |]
            )

            match AudioDecode.readWavSamples wave with
            | Error _ -> ()
            | Ok _ -> failwith "expected an error for a WAVE with a missing WAVE tag"
        })

[<Fact>]
let ``readWavSamples rejects a non-16-bit WAV`` () =
    withTempDir (fun dir ->
        task {
            let wav = Path.Combine(dir, "bits24.wav")
            use bw = new BinaryWriter(File.Create wav)

            bw.Write [| 0x52uy; 0x49uy; 0x46uy; 0x46uy |]
            bw.Write 36
            bw.Write [| 0x57uy; 0x41uy; 0x56uy; 0x45uy |]
            bw.Write [| 0x66uy; 0x6duy; 0x74uy; 0x20uy |]
            bw.Write 16
            bw.Write(int16 1)
            bw.Write(int16 1)
            bw.Write 16000
            bw.Write 48000
            bw.Write(int16 6)
            bw.Write(int16 24)
            bw.Write [| 0x64uy; 0x61uy; 0x74uy; 0x61uy |]
            bw.Write 0

            match AudioDecode.readWavSamples wav with
            | Error _ -> ()
            | Ok _ -> failwith "expected an error for a 24-bit WAV"
        })

[<Fact>]
let ``malformed bytes yield a decode error without loading a model`` () =
    task {
        let options =
            { baseOptions with
                ModelPath = Path.Combine(dataDir, "does-not-exist.onnx") }

        use stt = new SttService(options, NullLogger<SttService>.Instance)
        let bytes = Text.Encoding.UTF8.GetBytes("this is not audio")
        let! result = (stt :> ISttService).TranscribeAsync bytes CancellationToken.None

        match result with
        | Error(SttError.DecodeFailed _) -> ()
        | Error(SttError.UnsupportedCodec _) -> ()
        | other -> failwith (sprintf "expected a decode error, got %A" other)
    }

[<Fact>]
let ``oversized bytes yield TooLarge`` () =
    task {
        let options = { baseOptions with MaxBytes = 1024L }
        use stt = new SttService(options, NullLogger<SttService>.Instance)
        let bytes = Array.create 2048 0uy
        let! result = (stt :> ISttService).TranscribeAsync bytes CancellationToken.None

        match result with
        | Error(SttError.TooLarge(maxBytes, actualBytes)) ->
            maxBytes |> should equal 1024L
            actualBytes |> should equal 2048L
        | other -> failwith (sprintf "expected TooLarge, got %A" other)
    }

[<Fact>]
let ``too long audio yields TooLong`` () =
    withTempDir (fun dir ->
        task {
            let wav = Path.Combine(dir, "long.wav")
            let sr = 16000
            let samples = Array.create (sr * 10) 0.1
            writeWav wav sr samples

            let options =
                { baseOptions with
                    MaxDurationSeconds = 1 }

            use stt = new SttService(options, NullLogger<SttService>.Instance)
            let! result = (stt :> ISttService).TranscribeAsync (File.ReadAllBytes wav) CancellationToken.None

            match result with
            | Error(SttError.TooLong(maxSeconds, actualSeconds)) ->
                maxSeconds |> should equal 1.0
                actualSeconds |> should be (greaterThan 1.0)
            | other -> failwith (sprintf "expected TooLong, got %A" other)
        })

[<Fact>]
let ``ffmpeg timeout yields DecodeFailed`` () =
    withTempDir (fun dir ->
        task {
            // A fake ffmpeg that hangs; the real ffprobe is copied so the probe
            // step succeeds and the timeout is observed at the decode step.
            let ffmpeg = Path.Combine(dir, "ffmpeg")
            File.WriteAllText(ffmpeg, "#!/bin/sh\nsleep 30\n")
            File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

            let ffprobe = Path.Combine(dir, "ffprobe")

            if File.Exists "/usr/bin/ffprobe" then
                File.Copy("/usr/bin/ffprobe", ffprobe)

            let options =
                { baseOptions with
                    FfmpegPath = ffmpeg
                    FfmpegTimeoutSeconds = 1 }

            use stt = new SttService(options, NullLogger<SttService>.Instance)
            let wav = Path.Combine(dir, "in.wav")
            writeWav wav 16000 [| 0.0; 0.1; 0.0 |]
            let! result = (stt :> ISttService).TranscribeAsync (File.ReadAllBytes wav) CancellationToken.None

            match result with
            | Error(SttError.DecodeFailed _) -> ()
            | other -> failwith (sprintf "expected DecodeFailed, got %A" other)
        })

[<Fact>]
let ``checksum mismatch fails validation`` () =
    task {
        requireModel modelPath
        requireModel tokensPath
        requireModel vadModelPath

        let options =
            { baseOptions with
                ModelSha256 = "deadbeef" }

        use stt = new SttService(options, NullLogger<SttService>.Instance)

        match (stt :> ISttService).Validate() with
        | Error msg -> msg.Contains("checksum mismatch") |> should equal true
        | Ok() -> failwith "expected validation to fail on a wrong checksum"
    }

[<Fact>]
let ``validate succeeds when the model and VAD files are present`` () =
    task {
        requireModel modelPath
        requireModel tokensPath
        requireModel vadModelPath

        use stt = new SttService(baseOptions, NullLogger<SttService>.Instance)

        match (stt :> ISttService).Validate() with
        | Ok() -> ()
        | Error msg -> failwith (sprintf "expected validation to pass, got %s" msg)
    }

[<Fact>]
let ``validate fails when a required model file is missing`` () =
    task {
        let options =
            { baseOptions with
                VadModelPath = Path.Combine(dataDir, "silero-does-not-exist.onnx") }

        use stt = new SttService(options, NullLogger<SttService>.Instance)

        match (stt :> ISttService).Validate() with
        | Error msg -> msg.Contains("missing") |> should equal true
        | Ok() -> failwith "expected validation to fail on a missing VAD model"
    }

// ---------------------------------------------------------------------------
// Model-dependent tests (require provisioned models; fail with a clear message
// when missing, consistent with the CI provision step)
// ---------------------------------------------------------------------------

[<Fact>]
[<Trait("Category", "SttModel")>]
let ``ru fixture transcribes with WER <= 0.15`` () =
    task {
        requireModel modelPath
        requireModel tokensPath
        requireModel vadModelPath

        if not (File.Exists ruExampleWav) then
            failwith (sprintf "STT fixture missing: %s — run scripts/provision-stt.sh" ruExampleWav)

        let bytes = readFixtureBytes ruExampleWav
        let! result = (gigaService.Value :> ISttService).TranscribeAsync bytes CancellationToken.None

        match result with
        | Ok sttResult ->
            let w = wer canonicalReference sttResult.Text
            sttResult.Engine |> should equal SttEngine.GigaAm
            w |> should be (lessThanOrEqualTo 0.15)
        | Error err -> failwith (sprintf "expected Ok, got %A" err)
    }

[<Fact>]
[<Trait("Category", "SttModel")>]
let ``whisper fallback engine transcribes the fixture`` () =
    task {
        requireModel modelPath
        requireModel tokensPath
        requireModel vadModelPath
        requireModel (Path.Combine(whisperDir, "small-encoder.int8.onnx"))
        requireModel (Path.Combine(whisperDir, "small-decoder.int8.onnx"))
        requireModel (Path.Combine(whisperDir, "small-tokens.txt"))

        if not (File.Exists ruExampleWav) then
            failwith (sprintf "STT fixture missing: %s — run scripts/provision-stt.sh" ruExampleWav)

        // Directly exercise the Whisper recognizer (the fallback engine) to
        // prove it loads and produces non-empty text on a real RU audio.
        match AudioDecode.readWavSamples ruExampleWav with
        | Error e -> failwith e
        | Ok(sr, samples) ->
            use recognizer =
                Whisper.createRecognizer
                    { baseOptions with
                        WhisperDir = Some whisperDir }

            let text = Whisper.transcribe recognizer samples sr
            text |> should not' (be EmptyString)
    }

[<Fact>]
[<Trait("Category", "SttModel")>]
let ``whisper-configured service produces non-empty text`` () =
    task {
        requireModel modelPath
        requireModel tokensPath
        requireModel vadModelPath
        requireModel (Path.Combine(whisperDir, "small-encoder.int8.onnx"))
        requireModel (Path.Combine(whisperDir, "small-decoder.int8.onnx"))
        requireModel (Path.Combine(whisperDir, "small-tokens.txt"))

        if not (File.Exists ruExampleWav) then
            failwith (sprintf "STT fixture missing: %s — run scripts/provision-stt.sh" ruExampleWav)

        let bytes = readFixtureBytes ruExampleWav
        let! result = (whisperService.Value :> ISttService).TranscribeAsync bytes CancellationToken.None

        match result with
        | Ok sttResult -> sttResult.Text |> should not' (be EmptyString)
        | Error err -> failwith (sprintf "expected Ok, got %A" err)
    }

[<Fact>]
[<Trait("Category", "SttModel")>]
let ``silence yields NoSpeech`` () =
    task {
        requireModel vadModelPath

        let! result =
            withTempDir (fun dir ->
                task {
                    let wav = Path.Combine(dir, "silence.wav")
                    let sr = 16000
                    let samples = Array.create (sr * 3) 0.0
                    writeWav wav sr samples

                    return!
                        (gigaService.Value :> ISttService).TranscribeAsync
                            (File.ReadAllBytes wav)
                            CancellationToken.None
                })

        match result with
        | Error SttError.NoSpeech -> ()
        | other -> failwith (sprintf "expected NoSpeech, got %A" other)
    }
