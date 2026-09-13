# Phase 1–3 (v2 — OMP) — закрытие

Дата: 2026-09-13. Ветка: `feat/phases-1-3` (коммиты `bf4e8c6` → `4eaece1` → `108b10e` → `e9e7fbe`). База: `master` `64bd277` (research-plan v2 — OMP). Слияние в master — за владельцем.

Реализованы первые три фазы v2-плана: **Phase 1 — Domain policies**, **Phase 2 — Encrypted storage и durable queue**, **Phase 3 — Telegram transport**. Вся работа — в отдельном worktree (правило REPO-RULES), реализация — саб-агентами (`Phase1Core`, `Phase1V2`, `Phase2StorageV2`, `Phase3Telegram2`), проверка — владельцем.

## Итог

| Фаза | Проект | Тесты | Покрытие (gate) |
|---|---|---|---|
| 1 | `src/Phos.Core` | 60 (CoreTests + smoke) | line 97.1% / branch 94.7% |
| 2 | `src/Phos.Storage` | 32 (StorageTests) | line 98.2% / branch 96.8% |
| 3 | `src/Phos.Telegram` | 61 (TelegramTests) | line 94.3% / branch 93.7% |
| Всего | — | **155** (0 failed) | total line 97.3% / branch 94.8% |

`bash scripts/ci.sh` → **EXIT 0** (Release 0 warnings, Fantomas, FSharpLint 0 warnings, все тесты, coverage-gate PASS).

## Phase 1 — Domain policies (`Phos.Core`, без I/O)

- `DomainTypes` (UserId/ChatId/ChatKind/UserRole/Origin/Chat/User), `Whitelist` (private — только whitelisted; группы/каналы — явный ChatId + fallback роль User), `ToolPolicy` (host tools: min-роль, неизвестный → default deny; `NeedsConfirmation` убран — confirm у OMP), `Chunker` (4096 UTF-16, entities rebase/сплит, ```фенсы не режутся), `SchedulePolicy` (Cronos 0.13.0, catch-up SkipMissed/CatchUpOnce, DST invalid→None / ambiguous→standard offset, occurrenceKey), `InboxStateMachine` (Pending/Claimed/Running/Completed/Failed/DeadLetter/**NeedsReview**; HostDied/ReviewedRetry — v2 §2.9), `OutboxStateMachine` (stable RandomId).
- FsCheck: chunk invariants, state transitions, whitelist/tool authorization, DST fixtures.
- **v2-правки применены**: удалены `PromptBudget` и `Rrf` (переехали в OMP); `NeedsReview` добавлен; ToolPolicy перецелен на host tools.
- Acceptance: Core без I/O (grep: нет System.IO/Net/process/SQLite), branch ≥90% — **94.7%**.

## Phase 2 — Encrypted storage и durable queue (`Phos.Storage`)

- Миграции (v2-схема): `schema_version`, `users` (с `workspace_path`), `command_inbox` (включая `needs_review`), `message_outbox`, `schedule_jobs`, `schedule_runs`, `backup_log`.
- `StorageExecutor`: single-writer (`WriteAsync`, SemaphoreSlim(1)) + bounded read pool, WAL, `busy_timeout` (DefaultTimeout в lock-step), `foreign_keys`, checkpoint policy (`wal_checkpoint(TRUNCATE)` каждые N + `CheckpointNow`); `Microsoft.Data.Sqlite` async = sync (документировано, v2 §2.7).
- Репозитории: `IUserRepository`, `ICommandInbox` (lease, claim атомарно, retry, dead-letter, needs_review/review_retry), `IMessageOutbox` (UNIQUE random_id, retry без дублей).
- **Нет** mcp_tokens/TokenCrypto — креды MCP в `agent.db` OMP (v2).
- Acceptance: durable command переживает закрытие без clean shutdown + reopen (`committed command survives executor disposed without clean shutdown and reopened` — PASS); ключи отсутствуют в DB/логах (`raw database bytes do not contain sentinel secret` — PASS).

## Phase 3 — Telegram transport (`Phos.Telegram`)

- `ITelegramTransport`/`TelegramTransport` (WTelegramClient 4.4.8): bot login только по `bot_token` (телефон/код/2FA не запрашиваются), отправка с **явным `random_id`** через `Invoke(TL.Methods.Messages_SendMessage)` (helper `SendMessageAsync` генерирует random_id внутри — не подходит для outbox), edit, voice download (`DownloadFileAsync`), конфиг-callback `api_id/api_hash/bot_token/session_pathname`, session-файл 0600/каталог 0700.
- `UpdateHandler`: dedupe (bounded LRU) → **whitelist до admission** → `/start` (upsert user + приветствие), `/ping` (pong), текст/voice → `CommandEnvelope`; никогда не ждёт LLM/OMP.
- `OutboxDelivery`: pending → send (stable random_id) → MarkSent; FLOOD_WAIT/SLOWMODE_WAIT → retry позже **тем же** random_id, без второго сообщения; MarkFailed attempts+1.
- `EntitySend` (4096 + entities rebase), `PeerCache` (access hashes, без секретов).
- Acceptance (fake transport, v2 §2.8: real Telegram — integration/nightly): allowlisted `/start` → `/ping` (PASS), duplicate update обработан один раз (PASS), voice bytes скачаны (PASS), phone/code/2FA не запрашиваются (PASS).

## Гейт и инструменты (правки этой ветки)

- `scripts/coverage-gate.py`: (1) починен парсинг coverlet — filenames относительно `<sources>`, `branch="True"` (регистр); (2) per-project матчинг `Phos.Core`/`Phos.Storage`/`Phos.Scheduler`; (3) required-список по v2: Core/Storage/Scheduler; (4) **пропуск F# compiler-generated state machines** (`<StartupCode$...>` — coverlet атрибутирует десятки синтетических условий, v2 §2.8 «исключается только generated code», review — этот документ).
- `tests/Phos.Tests/coverlet.runsettings`: исключён только interop-обёртка `TelegramTransport.fs` (нельзя покрыть без реальной сети).
- `ToolPolicy.rank`: `[<MethodImpl(NoInlining)>]` — иначе F# инлайнит match и coverlet атрибутирует ветки 0% на определении (артефакт).
- Удалены мёртвые ветки: `OutboxDelivery` idempotency-guard (NextPending не возвращает Sent), `UpdateDedupe` null-ветка (инвариант capacity ≥ 1).

## Открытые пункты (не блокеры фазы)

1. Реальные Telegram-тесты (`/start` → `/ping` на живом боте, voice через MTProto) — **integration/nightly** по v2 §2.8; fast CI — fake transport.
2. `UpdateHandler.workspacePath` — `/var/lib/phos/workspace/<uid>` (v2 §2.9; финальный путь — настройка App).
3. Session-file rotation/backup bot-token — Phase 7/8.
4. Слияние `feat/phases-1-3` в master — владелец (правило REPO-RULES).
