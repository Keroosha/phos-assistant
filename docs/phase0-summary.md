# Phase 0 — закрытие: gates и технические spikes

Дата: 2026-09-12. Репозиторий: `phos-assistant`.

## 1. Gates (реализовано и проверено)

| Что | Где | Проверка |
|---|---|---|
| SDK pin `10.0.103` (`latestPatch`) | `global.json` | `dotnet --version` → 10.0.103 |
| `TreatWarningsAsErrors`, `Nullable` (F# → `--checknulls+`), `Deterministic` | `Directory.Build.props` | build 0 warnings/errors; FS3261/nullness подтверждён probe (null-возврат → ошибка сборки) |
| Central package pinning | `Directory.Packages.props` | FSharp.Core 10.0.103 explicit (без него MSB3277 конфликт FSharp.Core 5.0.2 из FsUnit) |
| Package source mapping (NU1507) | `NuGet.config` | restore чистый |
| Fantomas 7.0.6 + FSharpLint (dotnet-fsharplint 0.27.1) | `.config/dotnet-tools.json`, `.fsharplint.json` | `dotnet fantomas . --check` exit 0; `fsharplint lint Phos.sln` 0 warnings |
| Coverage gate: line ≥ 90%, branch ≥ 85% total; branch ≥ 90% для Core/Pipeline/Scheduler | `scripts/coverage-gate.py` | self-test: фикстура 50% → exit 1; 95% → exit 0; интеграция с coverlet cobertura — OK |
| CI-цепочка | `scripts/ci.sh`, `.github/workflows/ci.yml` | `bash scripts/ci.sh` → EXIT 0 (build, fantomas, lint, 2 теста, gate) |
| Тестовый стек | xunit 2.9.3, FsUnit.Xunit 6.0.0, FsCheck.Xunit 3.4.0, Microsoft.NET.Test.Sdk 18.10.0, coverlet.collector 10.0.1 | 2 теста (harness smoke) проходят; FsCheck 100 cases |

Замечания:
- **FsUnit.Xunit 7.x несовместим с xUnit v2** (тянет xunit.v3) — пин 6.0.0 (xunit 2.5.3+), как требует план («xUnit v2 + FsUnit»).
- **F# + CPM**: у F#-проектов пропадает implicit FSharp.Core → явный пин в центральном файле обязателен.
- `Phos.Core` пока без кода (фаза 1) — coverage-gate в этом состоянии выводит note и проходит (зафиксировано поведение).

## 2. Spikes (все с записанным фактическим выводом)

| Spike | Док | Результат | Blockers |
|---|---|---|---|
| vanbukin tool call + streaming (Microsoft.Extensions.AI.OpenAI 10.10.0 / OpenAI 2.13.0) | `docs/spikes/spike-01-openai-tool-stream.md` | streaming работает (TTFT 4.9 s у reasoning-модели), tool call roundtrip работает (`finish_reason: tool_calls` → `FunctionResultContent` → финальный ответ), tool-контент стримится | нет; зафиксированы: API 10.x (`GetResponseAsync`, `FunctionCallContent`), **проблема имён параметров** (`delegateArg0` из F#-делегата) — решить до production |
| multilingual-e5-small .NET vs Python parity | `docs/spikes/spike-02-embedding-parity.md` | **cos = 1.000000, 10/10 токенов** (RU/EN, query/passage) | нет; **критично**: `SentencePieceTokenizer` даёт raw SentencePiece id → обязателен XLM-R remap (`<s>`1→0, `<unk>`0→3, куски +1, pad=1) |
| GigaAM v3 CTC int8 (sherpa-onnx 1.13.8) CPU benchmark | `docs/spikes/spike-03-stt-benchmark.md` | p50 RTF: 1T 0.072 / 2T 0.040 / 4T 0.024 / 8T 0.017 / 16T 0.016; peak RSS ~693 MiB; текст корректный | нет; рекомендация num_threads=4; API: `OfflineRecognizer.from_nemo_ctc` |
| LUKS/systemd credential reboot procedure | — | **Удалён по решению владельца** (2026-09-12): вырезан из Phase 0, `§2.6 Encryption at rest`, строки рисков | — |

## 3. Принятие фазы 0

- [x] SDK/packages запинены; WarningAsErrors, nullable, Fantomas, FSharpLint, coverage gates — реализованы и проходят (`scripts/ci.sh`, EXIT 0).
- [x] Все spike имеют записанный фактический вывод (`docs/spikes/*.md`).
- [x] Ни один spike не является failed blocker — смена технологий не требуется (стек подтверждён: Microsoft.Extensions.AI.OpenAI + vanbukin, ONNX e5, GigaAM v3).
- [x] Фаза 0 закрыта. Следующая фаза — **Phase 1 (Domain policies)**, зависимость `0 → 1`.

## 4. Открытые вопросы к Phase 1+

1. Имена параметров инструментов в F# (п.2 spike 1) — контракт до Phos.Agent.
2. В БД хранить HF-ids (после remap) — зафиксировать в Phos.Memory при реализации.
3. SHA256 пина `model.int8.onnx` GigaAM v3 — при Phase 5 (файл в `spikes/spike-03-stt/data/`, gitignored).
