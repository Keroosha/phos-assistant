namespace Phos.Speech

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open FsToolkit.ErrorHandling

/// Typed audio-decode failures. `ProbeFailed`/`DecodeFailed` carry a short
/// stderr tail from the ffprobe/ffmpeg process.
type AudioDecodeError =
    | ProbeFailed of string
    | DecodeFailed of string

/// Parsed ffprobe output for one audio file.
type ProbeInfo =
    { DurationSeconds: float
      Codec: string }

/// Safe ffmpeg/ffprobe subprocess handling: `ArgumentList` only (no shell, no
/// string interpolation into a shell), `RedirectStandardError`, and kill of the
/// entire process tree on timeout. Pure helpers (`buildFfmpegArgs`,
/// `parseProbeJson`, `isSupportedCodec`) are exported so they are unit-testable
/// without invoking a real binary.
module AudioDecode =

    /// Runs a process with an argument list, returning `(stdout, stderr)` on
    /// success. On a non-zero exit or timeout returns an `Error` with a short
    /// stderr tail. No shell is involved: `ArgumentList` quotes arguments
    /// directly, so file names are never interpolated.
    let private runProcess (exe: string) (args: string list) (timeoutSeconds: int) : Result<string * string, string> =
        let psi = ProcessStartInfo(exe)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        for a in args do
            psi.ArgumentList.Add a

        try
            use proc = new Process()
            proc.StartInfo <- psi

            if not (proc.Start()) then
                Error(sprintf "failed to start %s" exe)
            else
                let stdoutTask = proc.StandardOutput.ReadToEndAsync()
                let stderrTask = proc.StandardError.ReadToEndAsync()

                if not (proc.WaitForExit(timeoutSeconds * 1000)) then
                    try
                        proc.Kill(entireProcessTree = true)
                    with _ ->
                        ()

                    Error(sprintf "%s timed out after %d s" exe timeoutSeconds)
                elif proc.ExitCode <> 0 then
                    let err = stderrTask.Result

                    let tail =
                        if String.IsNullOrWhiteSpace err then
                            ""
                        else
                            err.Trim().Substring(0, min 400 (err.Trim().Length))

                    Error(sprintf "%s exited %d: %s" exe proc.ExitCode tail)
                else
                    Ok(stdoutTask.Result, stderrTask.Result)
        with ex ->
            Error ex.Message

    /// Builds the `ffmpeg` argument list that decodes `inFile` to a mono
    /// 16 kHz PCM s16le WAV at `outWav`.
    let buildFfmpegArgs (outWav: string) (inFile: string) : string list =
        [ "-y"
          "-loglevel"
          "error"
          "-i"
          inFile
          "-ar"
          "16000"
          "-ac"
          "1"
          "-c:a"
          "pcm_s16le"
          outWav ]

    /// Codecs accepted for decode. Anything else is `UnsupportedCodec`. The set
    /// covers the common Telegram voice/audio containers and PCM variants that
    /// ffprobe may report.
    let isSupportedCodec (codec: string) : bool =
        match codec.Trim().ToLowerInvariant() with
        | "opus"
        | "ogg"
        | "vorbis"
        | "mp3"
        | "aac"
        | "wav"
        | "pcm_s16le"
        | "pcm_s24le"
        | "pcm_s32le"
        | "pcm_u8"
        | "pcm_f32le"
        | "pcm_f64le" -> true
        | _ -> false

    /// Parses `ffprobe -print_format json` output into `ProbeInfo`.
    let parseProbeJson (json: string) : Result<ProbeInfo, string> =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement

            let duration =
                match root.TryGetProperty "format" with
                | true, fmt ->
                    match fmt.TryGetProperty "duration" with
                    | true, d ->
                        match d.ValueKind with
                        | JsonValueKind.Number -> d.GetDouble()
                        | JsonValueKind.String ->
                            let s = Option.ofObj (d.GetString()) |> Option.defaultValue "0"
                            Double.Parse(s, Globalization.CultureInfo.InvariantCulture)
                        | _ -> 0.0
                    | _ -> 0.0
                | _ -> 0.0

            let codec =
                match root.TryGetProperty "streams" with
                | true, streams when streams.ValueKind = JsonValueKind.Array ->
                    let mutable codec = ""

                    for stream in streams.EnumerateArray() do
                        let isAudio =
                            match stream.TryGetProperty "codec_type" with
                            | true, t -> Option.ofObj (t.GetString()) = Some "audio"
                            | _ -> false

                        if isAudio && String.IsNullOrEmpty codec then
                            match stream.TryGetProperty "codec_name" with
                            | true, c -> codec <- Option.ofObj (c.GetString()) |> Option.defaultValue ""
                            | _ -> ()

                    codec
                | _ -> ""

            if String.IsNullOrEmpty codec then
                Error "no audio stream found in probe output"
            else
                Ok
                    { DurationSeconds = duration
                      Codec = codec }
        with ex ->
            Error ex.Message

    /// Probes `inFile` with ffprobe (JSON output) and parses duration + codec.
    /// The ffprobe binary is derived from `SttOptions.FfmpegPath` (same dir).
    let probe (options: SttOptions) (inFile: string) : Result<ProbeInfo, AudioDecodeError> =
        let probeExe =
            let dir = Option.ofObj (Path.GetDirectoryName options.FfmpegPath)

            match dir with
            | Some d when d <> "" -> Path.Combine(d, "ffprobe")
            | _ -> "ffprobe"

        let args =
            [ "-v"
              "error"
              "-print_format"
              "json"
              "-show_format"
              "-show_streams"
              inFile ]

        match runProcess probeExe args options.ProbeTimeoutSeconds with
        | Ok(stdout, _) -> parseProbeJson stdout |> Result.mapError ProbeFailed
        | Error e -> Error(ProbeFailed e)

    /// Decodes `inFile` to a mono 16 kHz PCM s16le WAV at `outWav`.
    let decodeToWav (options: SttOptions) (inFile: string) (outWav: string) : Result<unit, AudioDecodeError> =
        match runProcess options.FfmpegPath (buildFfmpegArgs outWav inFile) options.FfmpegTimeoutSeconds with
        | Ok _ -> Ok()
        | Error e -> Error(DecodeFailed e)

    /// Reads a PCM s16le mono WAV into a `float32` sample array (range -1..1)
    /// together with the sample rate. Used on the ffmpeg-decoded 16 kHz output.
    let readWavSamples (wavPath: string) : Result<int * float32[], string> =
        try
            use br = new BinaryReader(File.OpenRead wavPath)

            let header (expected: byte[]) = br.ReadBytes expected.Length = expected

            if not (header [| 0x52uy; 0x49uy; 0x46uy; 0x46uy |]) then
                Error "not a RIFF file"
            else
                ignore (br.ReadInt32()) // RIFF chunk size

                if not (header [| 0x57uy; 0x41uy; 0x56uy; 0x45uy |]) then
                    Error "not a WAVE file"
                else
                    let mutable sampleRate = 0
                    let mutable bits = 0
                    let mutable data = Array.empty<byte>
                    let mutable finished = false

                    while not finished do
                        let id = br.ReadBytes 4

                        if id.Length < 4 then
                            finished <- true
                        else
                            let chunkSize = br.ReadInt32()
                            let idStr = Text.Encoding.ASCII.GetString id

                            if idStr = "fmt " then
                                let fmt = br.ReadBytes chunkSize
                                ignore (BitConverter.ToInt16(fmt, 0))
                                sampleRate <- BitConverter.ToInt32(fmt, 4)
                                ignore (BitConverter.ToInt16(fmt, 8))
                                bits <- int (BitConverter.ToInt16(fmt, 14))
                            elif idStr = "data" then
                                data <- br.ReadBytes chunkSize
                                finished <- true
                            else
                                br.BaseStream.Seek(int64 chunkSize, SeekOrigin.Current) |> ignore

                    if sampleRate <= 0 then
                        Error "missing sample rate"
                    elif bits <> 16 then
                        Error(sprintf "expected 16-bit PCM, got %d-bit" bits)
                    else
                        let n = data.Length / 2
                        let samples = Array.zeroCreate<float32> n

                        for i in 0 .. n - 1 do
                            samples.[i] <- float32 (BitConverter.ToInt16(data, i * 2)) / 32768.0f

                        Ok(sampleRate, samples)
        with ex ->
            Error ex.Message
