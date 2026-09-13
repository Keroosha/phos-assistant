# Phase 1 (v2) — план реализации и контракт

Дата: 2026-09-13. Ветка: `feat/phases-1-3`. Основа: `docs/research-plan.md` **v2 (OMP)** — §2.1, §2.5, §2.6, §2.8, §2.9, Phase 1.

Статус: Phase 1 реализована по v1 и **приводится в соответствие v2**; v2 Phase 2 (Storage) и Phase 3 (Telegram transport) — следующие фазы, НЕ входят в текущий скоуп.

---

## Общие правила (обязательные для агента)

- Работать в worktree `/home/keroosha/kitchen/experimental/phos-assistant-p1-3` (ветка `feat/phases-1-3`, rebased на master с v2-планом).
- **Не коммитить в master, не делать merge.** Коммиты в фиче-ветку делает владелец сессии после проверки; агент оставляет изменения незакоммиченными.
- TDD: failing test → implementation → green. FsCheck для чистых механик.
- Финальная проверка: `bash scripts/ci.sh` → EXIT 0 (build Release 0 warnings, `dotnet fantomas . --check`, `dotnet dotnet-fsharplint lint Phos.sln`, `dotnet test --collect:"XPlat Code Coverage"`, `python3 scripts/coverage-gate.py tests/*/TestResults`).
- `#nowarn` — только минимальный scope + комментарий.
- Formatting: Fantomas (4 пробела). Линт: `.fsharplint.json`.

---

## Phase 1 (v2) — Domain policies (`src/Phos.Core`, БЕЗ I/O)

Критерий: Core не содержит I/O (нет System.IO, SQLite, network, процессов, file system). Пакет: `Cronos` 0.13.0 (уже запинен).

### Что меняется относительно v1-реализации (commit bf4e8c6)

| Модуль | v1 | v2 |
|---|---|---|
| `PromptBudget.fs` | реализован | **УДАЛИТЬ** (промпт-сборка/бюджеты — OMP: SYSTEM.md/APPEND_SYSTEM.md/skills; v2 §0 «Снятые пункты») |
| `Rrf.fs` | реализован | **УДАЛИТЬ** (память/recall — OMP mnemopi; v2 §0 «BM25/RRF/embedding-паритет… больше не исследуем») |
| `ToolPolicy.fs` | Allow/Prompt/Deny для MCP-инструментов | **ПЕРЕЦЕЛИТЬ** на host tools (v2 §2.5, §2.9, Phase 5): `tg_send_message`, `tg_edit_message`, `stt_transcribe` и др. Ниже — точная сигнатура |
| `InboxStateMachine.fs` | без NeedsReview | **ДОБАВИТЬ** `NeedsReview` (v2 §2.9: смерть хоста / unknown host-tool side effect → `needs_review`, не автоповтор) |
| `DomainTypes.fs` | v1 | без изменений (workspace_path — забота Phase 2/users-репозитория, не Core) |
| `Whitelist.fs`, `Chunker.fs`, `SchedulePolicy.fs`, `OutboxStateMachine.fs` | v1 | без изменений |

### Точные сигнатуры (обязательные, после правок)

1. `DomainTypes.fs` — без изменений: UserId, ChatId, ChatKind, UserRole, Origin, Chat, User.

2. `Whitelist.fs` — без изменений: Whitelist, DenialReason, Decision, create, authorize.

3. `ToolPolicy.fs` — `module Phos.Core.ToolPolicy`:
   - `type Policy = { DefaultMinRole: UserRole option; Tools: Map<string, UserRole> }`
     - `Tools`: host tool name → минимальная роль (Owner > Admin > User). Известный инструмент доступен роли, если `role >= minRole`. Неизвестный инструмент → `DefaultMinRole` (`None` = всегда denied).
   - `type Decision = { Allowed: bool; MinRole: UserRole option }` — `Allowed=false` + `MinRole=None` для denied; `Allowed=true` + `MinRole=Some min` для allowed.
   - `val resolve: Policy -> UserRole -> toolName: string -> Decision`
   - `val roleAtLeast: UserRole -> UserRole -> bool`
   - Убрать `NeedsConfirmation` (confirm — ответственность OMP для MCP; host tools не требуют).
   - FsCheck: роли/порядок (Owner >= Admin >= User), известный/неизвестный инструмент, DefaultMinRole, границы.

4. `Chunker.fs` — без изменений.

5. `SchedulePolicy.fs` — без изменений.

6. `InboxStateMachine.fs` — `module Phos.Core.InboxStateMachine`:
   - `type Status = Pending | Claimed | Running | Completed | Failed | DeadLetter | NeedsReview`
   - `type Event = Claim of DateTimeOffset * DateTimeOffset | Start | Complete | Fail | LeaseExpired of DateTimeOffset | Retry | DeadLetter | HostDied | ReviewedRetry`
   - Переходы (плюс к v1):
     - `Claimed --HostDied--> NeedsReview`; `Running --HostDied--> NeedsReview` (ход мог частично выполниться — не автоповтор);
     - `NeedsReview --ReviewedRetry--> Pending` (явное ручное решение; attempts сохраняются);
     - `NeedsReview --DeadLetter--> DeadLetter`;
     - все прочие события из `NeedsReview` → Error.
   - Остальные переходы v1 без изменений.
   - FsCheck: HostDied из Claimed/Running; из NeedsReview нет автоперехода (только ReviewedRetry/DeadLetter); полный набор invalid-переходов.

7. `OutboxStateMachine.fs` — без изменений.

### Тесты (`tests/Phos.Tests/CoreTests.fs`)

- УДАЛИТЬ: RRF-свойства (monotonicity) и prompt-budget факты/свойства.
- ДОБАВИТЬ: NeedsReview-переходы (см. выше), ToolPolicy-свойства (см. выше).
- ОСТАВИТЬ: chunk invariants, whitelist authorization, schedule/DST fixtures, inbox/outbox state machine (кроме удалённых/изменённых).
- Остальные файлы тестов (`Tests.fs`) не трогать.

---

## Phase 2 (v2) — контур (НЕ реализуется сейчас)

- `src/Phos.Storage`: миграции (users c `workspace_path`, command_inbox, message_outbox, schedule_jobs, schedule_runs, backup_log), storage executor (single-writer + read pool, WAL, busy_timeout, checkpoint), leases, репозитории inbox/outbox.
- **Нет** `mcp_servers`/`mcp_tokens`/TokenCrypto — креды MCP в `agent.db` OMP.
- Acceptance «keys отсутствуют в DB/логах» = секреты phos (bot token, API-key провайдера) не попадают в DB/логи.

## Phase 3 (v2) — контур (НЕ реализуется сейчас)

- `src/Phos.Telegram`: bot login (WTelegramClient), UpdateManager, peer cache, voice download, entity-safe send/edit/live draft, FLOOD_WAIT, random-id outbox delivery; whitelist до queue admission.
- Acceptance: allowlisted `/start` → `/ping`; duplicate update один раз; voice bytes скачаны; phone/code/2FA не запрашиваются.

---

## Порядок работ

1. Владелец: merge v2 в master (сделано), rebase фиче-ветки (сделано), обновить `scripts/coverage-gate.py` (Core/Storage/Scheduler — сделано).
2. Агент: привести Phase 1 в соответствие v2 (таблица выше) → `scripts/ci.sh` EXIT 0.
3. Владелец: проверка, коммит в `feat/phases-1-3`, сводка.
