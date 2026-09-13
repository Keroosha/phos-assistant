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

## Phase 2 (v2) — Encrypted storage и durable queue (`src/Phos.Storage`)

Пакеты: `Microsoft.Data.Sqlite` 10.0.12 (pin в `Directory.Packages.props`). Проект `src/Phos.Storage/Phos.Storage.fsproj` (FSharp.Core + Microsoft.Data.Sqlite), ProjectReference на `Phos.Core`. Добавить в `Phos.sln`. Ссылается на: `Phos.Core.DomainTypes`, `Phos.Core.InboxStateMachine.Status`, `Phos.Core.OutboxStateMachine.Status`, `Phos.Core.UserRole`.

Файлы (порядок в fsproj):

1. `Schema.fs` — `type Migration = { Version: int; Name: string; Sql: string }`; `val migrate: StorageExecutor -> Migration list -> Task<int>` (идемпотентно, каждая миграция в транзакции, `schema_version`). DDL (v2-схема, timestamps INTEGER unix seconds UTC):
   - `schema_version(version INTEGER NOT NULL, applied_at INTEGER NOT NULL)`
   - `users(user_id INTEGER PRIMARY KEY, username TEXT, role TEXT NOT NULL, workspace_path TEXT NOT NULL, timezone TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL)`
   - `command_inbox(id INTEGER PRIMARY KEY AUTOINCREMENT, origin TEXT NOT NULL, external_key TEXT UNIQUE, user_id INTEGER NOT NULL, chat_id INTEGER NOT NULL, payload TEXT NOT NULL, priority INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT 'pending', lease_until INTEGER NULL, heartbeat_at INTEGER NULL, attempts INTEGER NOT NULL DEFAULT 0, max_attempts INTEGER NOT NULL DEFAULT 5, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL)`
   - `message_outbox(id INTEGER PRIMARY KEY AUTOINCREMENT, command_id INTEGER NOT NULL, chunk_index INTEGER NOT NULL, chat_id INTEGER NOT NULL, random_id INTEGER NOT NULL UNIQUE, payload TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'pending', remote_message_id INTEGER NULL, attempts INTEGER NOT NULL DEFAULT 0, max_attempts INTEGER NOT NULL DEFAULT 5, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, UNIQUE(command_id, chunk_index))`
   - `schedule_jobs(id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INTEGER NOT NULL, cron_expr TEXT NOT NULL, timezone TEXT NOT NULL, prompt TEXT NOT NULL, enabled INTEGER NOT NULL DEFAULT 1, catchup_policy TEXT NOT NULL DEFAULT 'skip', next_run INTEGER NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL)`
   - `schedule_runs(job_id INTEGER NOT NULL, scheduled_for INTEGER NOT NULL, status TEXT NOT NULL, command_id INTEGER NULL, PRIMARY KEY(job_id, scheduled_for))`
   - `backup_log(id INTEGER PRIMARY KEY AUTOINCREMENT, started_at INTEGER NOT NULL, finished_at INTEGER NULL, path TEXT NULL, checksum TEXT NULL, status TEXT NOT NULL, error TEXT NULL)`
   - Статусы inbox: 'pending'/'claimed'/'running'/'completed'/'failed'/'dead_letter'/'needs_review' (маппинг на Core Status). Outbox: 'pending'/'sending'/'sent'/'failed'.

2. `StorageExecutor.fs`:
   - `type StorageOptions = { DatabasePath: string; BusyTimeout: TimeSpan; ReadPoolSize: int; CheckpointEvery: int }`
   - `type StorageExecutor` — единственный writer: `WriteAsync: (SqliteConnection -> 'T) -> Task<'T>` (сериализовано SemaphoreSlim(1)); `ReadAsync: (SqliteConnection -> 'T) -> Task<'T>` (bounded pool); соединения между потоками не разделяются; `PRAGMA journal_mode=WAL`, `busy_timeout`, `foreign_keys=ON`; checkpoint `wal_checkpoint(TRUNCATE)` после каждых `CheckpointEvery` writes + `CheckpointNow()`; `IAsyncDisposable`; фабрика `StorageExecutor.Create` / `openExecutor`.

3. `Users.fs` — `IUserRepository` + SQLite:
   - `type UserRecord = { Id: UserId; Username: string option; Role: UserRole; WorkspacePath: string; Timezone: string option }`
   - `Upsert: UserRecord -> Task<unit>`, `GetByTelegramId: UserId -> Task<UserRecord option>`, `List: unit -> Task<UserRecord list>`.

4. `CommandInbox.fs` — `ICommandInbox` + SQLite (как в v1-контракте, плюс needs_review):
   - `type CommandEnvelope = { Origin: Origin; ExternalKey: string option; UserId: UserId; ChatId: ChatId; Payload: string; Priority: int }`
   - `type Lease = { Until: DateTimeOffset; HeartbeatAt: DateTimeOffset }`
   - `type Command = { Id: int64; Envelope: CommandEnvelope; Status: InboxStateMachine.Status; Attempts: int; MaxAttempts: int; LeaseUntil: DateTimeOffset option; HeartbeatAt: DateTimeOffset option }`
   - `Insert` (UNIQUE external_key → вернуть существующий id), `ClaimNextForChat: ChatId -> Lease -> Task<Command option>` (атомарно, один победитель), `ClaimById`, `MarkStarted`, `MarkCompleted`, `MarkFailed` (attempts+1 → failed/dead_letter по max_attempts), `Retry` (failed → pending), `MarkNeedsReview` (claimed/running → needs_review), `ReviewRetry` (needs_review → pending), `Heartbeat`, `ExpireLeases: DateTimeOffset -> Task<int>`, `CountPending`, `CountDeadLetter`.

5. `MessageOutbox.fs` — `IMessageOutbox` + SQLite (как v1: Insert UNIQUE random_id/command+chunk, BeginSend, MarkSent(id, remoteMessageId), MarkFailed, Retry (random_id неизменен, без дублей), NextPending, GetByRandomId, CountPending).

6. `Repositories.fs` — фабрики/DI (по вкусу).

**НЕТ** `mcp_servers`/`mcp_tokens`/TokenCrypto/IMasterKeyProvider — креды MCP в `agent.db` OMP (v2).

Тесты (`tests/Phos.Tests/StorageTests.fs`), temp-file SQLite с WAL:
- миграции: fresh → latest; повторный запуск идемпотентен; upgrade replay;
- concurrency: N параллельных Insert/Claim через WriteAsync — все закоммичены, без потерь; busy_timeout ограничивает ожидание (~2s, не вешает);
- crash boundaries: транзакция без commit (dispose) → rollback; committed данные переживают закрытие/переоткрытие (WAL replay);
- leases: два конкурентных claim → ровно один; expiry → pending; attempts ≥ max → dead_letter; `MarkNeedsReview` + `ReviewRetry` (needs_review → pending);
- restart replay: pending после reopen остаётся claimable;
- outbox: retry сохраняет random_id; duplicate random_id → одна запись; MarkSent хранит remote_message_id;
- users: Upsert/GetByTelegramId/List с workspace_path;
- **keys absent**: сырые байты DB-файла не содержат sentinel-секретов (например, `StorageOptions`/connection string с паролем-сентinel); вывод (stdout/stderr, если storage логирует) не содержит payload/секреты.

Acceptance: durable command переживает process kill (committed → закрыть без clean shutdown → переоткрыть → claimable); ключи отсутствуют в DB/логах; `bash scripts/ci.sh` EXIT 0 с гейтом (Storage branch ≥ 90% — Storage теперь в required-проектах v2).

## Phase 3 (v2) — Telegram transport (`src/Phos.Telegram`)

Пакет: `WTelegramClient` 4.4.8 (pin в `Directory.Packages.props`). Проект `src/Phos.Telegram/Phos.Telegram.fsproj` (FSharp.Core + WTelegramClient), ProjectReference на `Phos.Core` и `Phos.Storage`. Добавить в `Phos.sln`.

Файлы (порядок в fsproj):

1. `Transport.fs` — граница, фейкабельная в тестах:
   - `type BotInfo = { BotId: int64; Username: string option }`
   - `type TelegramEntity = { Offset: int; Length: int; Kind: EntityKind }` (переиспользовать `Phos.Core.Chunker.EntityKind`; offsets UTF-16 — совпадает с Telegram)
   - `type SendTarget = { ChatId: ChatId; RandomId: int64; Text: string; Entities: TelegramEntity list }`
   - `type SendResult = { RemoteMessageId: int64 }`
   - `type VoiceRef = { ChatId: ChatId; MessageId: int64; FileReference: byte[]; AccessHash: int64 }`
   - `type ITelegramTransport =
       abstract Login: unit -> Task<BotInfo>` (только bot token; телефон/код/2FA не запрашиваются)
       `abstract SendMessage: SendTarget -> Task<SendResult>` (stable random_id)
       `abstract EditMessage: ChatId -> int64 -> text: string -> entities: TelegramEntity list -> Task<unit>`
       `abstract DownloadVoice: VoiceRef -> Task<byte[]>`
   - `TelegramTransport` — реализация поверх WTelegramClient (см. `docs/research-plan.md` §2.1: `wtConfig` callback, `LoginBotIfNeeded`, MTProto-методы для ботов). Верифицировать API по `~/.nuget/packages/wtelegramclient/4.4.8/lib/*/WTelegramClient.xml`.

2. `PeerCache.fs` — кэш peer/access hashes: `type PeerCache = ...` — `Get/Cache: ChatId -> InputPeer option`; потокобезопасный; access_hash из updates; для новых peer — refetch. НЕ хранит секреты.

3. `UpdateModel.fs` — `type IncomingUpdate = { UpdateId: int64; Chat: Chat; From: User; Text: string option; Voice: VoiceRef option }` + маппер из TL-объектов WTelegramClient (message/update).

4. `UpdateHandler.fs` — `type HandleResult = Accepted | Duplicate | Denied of DenialReason | AdmitFailed`
   - `type UpdateHandler (whitelist: Whitelist, inbox: ICommandInbox, dedupe: UpdateDedupe, admit: CommandEnvelope -> Task<AdmitOutcome>)`
   - `HandleAsync: IncomingUpdate -> Task<HandleResult>`: (1) dedupe по `UpdateId`; (2) **whitelist до queue admission** (`Whitelist.authorize`); (3) classify: `/start` → upsert user + приветствие через outbox; `/ping` → `pong` через outbox; обычный текст/voice → `CommandEnvelope` в `command_inbox` (через переданный admit). Никогда не ждёт LLM/OMP.

5. `UpdateDedupe.fs` — bounded LRU по `UpdateId` (последние N=1000; после рестарта Telegram offset решает; документировать).

6. `OutboxDelivery.fs` — `type OutboxDelivery (outbox: IMessageOutbox, transport: ITelegramTransport, logger)`
   - `RunAsync: CancellationToken -> Task<unit>`: `NextPending` → `SendMessage` (stable random_id) → `MarkSent(remoteMessageId)`; `FLOOD_WAIT`/`SLOWMODE_WAIT` → отложить retry (не создавать новое сообщение); `MarkFailed` → attempts+1, retry тем же random_id; идемпотентность: `GetByRandomId` перед повторной отправкой.

7. `EntitySend.fs` — `val chunkForSend: text: string -> entities: TelegramEntity list -> Chunk list` (обёртка над `Chunker.chunk` 4096); `val toTelegramEntities: Chunk -> TelegramEntity list` (rebased offsets); live-draft limits (20/5s, 40/30s — конфиг, константы).

Тесты (`tests/Phos.Tests/TelegramTests.fs`), fake transport (реализует `ITelegramTransport`, записывает вызовы; fake `ICommandInbox`/`IMessageOutbox` — in-memory или temp SQLite):
- allowlisted `/start` → accept + user upsert; `/ping` → outbox `pong` (fake transport получил `SendTarget` с текстом);
- duplicate update (same UpdateId) → `Duplicate`, обработан один раз (один admit/send);
- whitelist: не-whitelisted → `Denied`, admit НЕ вызван (проверка до admission);
- voice update → admit с voice-маркером; `DownloadVoice` возвращает байты;
- outbox delivery: `SendMessage` с тем же `random_id` при retry; `MarkSent` после успеха; FLOOD_WAIT → retry без второго сообщения;
- chunk/entity: текст >4096 → несколько чанков ≤4096, entities rebased; entity-safe send;
- login: контракт Login не запрашивает phone/code/2FA (fake config callback проверяет: только api_id/api_hash/bot_token/session_pathname).

Acceptance (фаза): allowlisted `/start` → `/ping` (fake-transport контрактный тест; реальный Telegram — integration/nightly, см. research-plan §2.8); duplicate update один раз; voice bytes скачаны; phone/code/2FA не запрашиваются; `bash scripts/ci.sh` EXIT 0 (гейт: Core/Storage branch ≥90% уже enforced; Telegram не в required-списке, но в totals line ≥90%/branch ≥85%).

---

## Порядок работ

1. Владелец: merge v2 в master (сделано), rebase фиче-ветки (сделано), обновить `scripts/coverage-gate.py` (Core/Storage/Scheduler — сделано).
2. Агент: привести Phase 1 в соответствие v2 (таблица выше) → `scripts/ci.sh` EXIT 0.
3. Владелец: проверка, коммит в `feat/phases-1-3`, сводка.
