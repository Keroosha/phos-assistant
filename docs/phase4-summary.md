# Phase 4 — Speech: summary

Local STT for Telegram voice messages: safe ffmpeg decode → Silero VAD → GigaAM v3
CTC primary → Whisper small fallback. Implemented in the new `Phos.Speech` project,
wired into `Phos.Telegram` (VoiceProcessor + UpdateHandler) and `Phos.App` (config,
DI, self-test hosted service).

## Deliverables

- `src/Phos.Speech/` — new project (net10.0, `org.k2fsa.sherpa.onnx` 1.13.8,
  linux-x64 runtime is transitive):
  - `SttTypes.fs` — `SttOptions`, `SttEngine`, `SttResult`, `SttError` + defaults.
  - `AudioDecode.fs` — ffprobe/ffmpeg subprocess (ArgumentList only, no shell),
    JSON probe parser, codec whitelist, mono 16 kHz PCM16 WAV reader. Pure helpers
    (`buildFfmpegArgs`, `parseProbeJson`, `isSupportedCodec`) are exported and
    unit-testable.
  - `SileroVad.fs` — Silero VAD via sherpa-onnx; `hasSpeech`/`detectSpeechSeconds`.
  - `GigaAm.fs` — GigaAM v3 CTC int8 recognizer (`nemo_ctc`).
  - `Whisper.fs` — Whisper small multilingual int8 recognizer (`ru`, transcribe).
  - `SttService.fs` — `ISttService` + `SttService`: lazy recognizer load
    (SemaphoreSlim), `MaxConcurrentStt` serialization, typed `Result` flow.
- `src/Phos.Telegram/` — `VoiceProcessor.fs` (`IVoiceProcessor` + `VoiceProcessor`),
  `UpdateHandler` takes `IVoiceProcessor`, voice update → transcript (no `voice:`
  prefix), error → `⚠️ <msg>` reply.
- `src/Phos.App/` — `SttSettings` config section + validation, `toSttOptions`,
  DI wiring, `SttSelfTest` hosted service (logs, never crashes).
- `appsettings.example.json` — `Stt` section mirroring defaults.
- `scripts/provision-stt.sh` — downloads + sha256-pins GigaAM, silero VAD, Whisper
  small, `long_example.wav`; cuts the WER fixture `ru_example.wav`; idempotent.
- `.github/workflows/ci.yml` — adds `Provision STT models` step before the gates.
- `tests/Phos.Tests/SpeechTests.fs` — 8 tests (WER, whisper, NoSpeech, malformed,
  TooLarge, TooLong, timeout, checksum, defaults).

## Acceptance mapping

| Acceptance | Result |
|---|---|
| Real RU fixture + WER | `ru_example.wav` → canonical reference, WER **0.0455** (1 word diff, ≤ 0.15) |
| p50/p95 RTF/RSS from spike doc | recorded on this CPU in `spikes/spike-03-stt-benchmark.md` (p50 RTF 0.024 @ 4 threads, peak RSS ~0.7 GiB) |
| Defaults follow measured data | `SttOptions.defaults.NumThreads = 4`, `MaxConcurrentStt = 1` |
| Model checksum pin | `ModelSha256 = f86ebfa…`; `Validate()` verifies sha256 |
| Whisper fallback | verified (non-empty text via Whisper recognizer + Whisper-configured service) |

## FeatureDim conclusion

**80 was used.** Empirically both `80` and `64` produce identical, correct output on
GigaAM v3 CTC int8 (verified with a scratch .NET program on `example.wav`). `80`
matches the benchmark's `from_nemo_ctc` default and the official `offline-decode`
example, so it is the documented/verified configuration.

## VAD approach

**Sherpa-onnx Silero VAD** (`VoiceActivityDetector`), not RMS fallback. The API is
usable from F# (verified). `detectSpeechSeconds` sums detected speech segments;
`hasSpeech` is `> 0.0`. Verified: `ru_example.wav` → ~6.1s speech, 3s silence → 0.

## Adaptations / decisions

1. **WER fixture is `ru_example.wav`, not `example.wav`.** The assignment stated
   `example.wav` produces the canonical reference «но в кельях…», but empirically
   `example.wav` transcribes to a different Pushkin reading («ничьих не требуя
   похвал…»). The «но в кельях…» passage is in `long_example.wav` (starts at 2.0s).
   `ru_example.wav` is a 10.5s cut of `long_example.wav` (sha256 `2c456b64…`),
   produced by `provision-stt.sh` from the downloadable HF source. Measured WER
   0.0455 (model says «убогий», reference says «убогой»).
2. **`IVoiceProcessor` interface** added so `UpdateHandler` is testable with a fake.
3. **`#nowarn "3511"`** in `SpeechTests.fs`: awaiting `Task<Result<…>>` then
   pattern-matching in the `task` CE cannot be statically compiled (F# falls back to
   a dynamic state machine). It is a compiler performance note, not a correctness
   issue; escalated to an error by `TreatWarningsAsErrors`.
4. **`ModelSha256` default** is the pinned GigaAM sha (`f86ebfa…`) per the spike.

## Known gap: no real RU/EN code-switch fixture

No real RU/EN code-switch recording exists in the repo. The WER test slot is
prepared: the fixture name `ru_en_example.wav` is not yet present, and there is no
reference-text constant for it. When the owner supplies a real recording
(`ru_en_example.wav` in the data dir) plus its reference text, add a test that
fails with a clear "STT fixture missing" message until the fixture exists. The
existing `requireModel`/missing-fixture `failwith` pattern is the precedent.

## Verification

- `dotnet build Phos.sln -c Release` → 0 warnings / 0 errors.
- `dotnet test Phos.sln -c Release --no-build` → **183 passed**, 0 failed, 0 skipped
  (existing suite + new SpeechTests; model-dependent tests ran since models are
  present). Coverage: total line 95.4%, branch 86.1%; Phos.Speech line 87.1%,
  branch 70.8% — `coverage-gate: PASS`.
- `scripts/provision-stt.sh` → downloads + sha256 verifies, idempotent, exit 0.
- `dotnet fantomas . --check` and `dotnet-fsharplint` → run as part of the gate.
