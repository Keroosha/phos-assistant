# Phase 1–3 — план реализации и межфазовые контракты

Дата: 2026-09-13. Ветка: `feat/phases-1-3`. Основа: `docs/research-plan.md` (§2.3, §2.6, §2.8, Phase 1–3).

Зависимость: `1 → 2 → 3` (Core → Storage → Pipeline).

---

## Общие правила (обязательные для каждого агента)

- Работать в worktree `/home/keroosha/kitchen/experimental/phos-assistant-p1-3`.
- **Не коммитить в master, не делать merge.** Коммиты в `feat/phases-1-3` делает владелец сессии после проверки; агент может оставлять изменения незакоммиченными.
- TDD: failing test → implementation → green. Каждый механизм покрыт тестами; FsCheck для чистых механик.
- Финальная проверка каждого агента: `bash scripts/ci.sh` (build Release 0 warnings, `dotnet fantomas . --check`, `dotnet dotnet-fsharplint lint Phos.sln`, `dotnet test --collect:"XPlat Code Coverage"`, `python3 scripts/coverage-gate.py tests/*/TestResults`) → EXIT 0. Итерировать до EXIT 0.
- `#nowarn` — только минимальный scope + комментарий (например, для nullness interop Microsoft.Data.Sqlite).
- Formatting: Fantomas (4 пробела). Линт: `.fsharplint.json` (relaxed, enabled: `redundantNewKeyword`, `reimplementsFunction`, `canBeReplacedWithComposition`, `usedUnderscorePrefixedElements`, `failwithWithSingleArgument`).
- F# файлы подключаются в `.fsproj` явно, порядок = порядок зависимостей (файл может ссылаться только на ранее объявленные).

---

## Phase 1 — Domain policies (`src/Phos.Core`, БЕЗ I/O)

Критерий: Core не содержит I/O (нет System.IO, SQLite, network, процессов, file system). Пакет: `Cronos` 0.13.0 (pure computation) — добавить в `Directory.Packages.props` и `Phos.Core.fsproj`.

Файлы (порядок в fsproj):

1. `DomainTypes.fs` — `module Phos.Core.DomainTypes`:
   - `type UserId = UserId of int64`
   - `type ChatId = ChatId of int64`
   - `type ChatKind = Private | Group | Channel`
   - `type UserRole = Owner | Admin | User`
   - `type Origin = Telegram | Schedule | System`
   - `type Chat = { Id: ChatId; Kind: ChatKind }`
   - `type User = { Id: UserId; Username: string option; Role: UserRole }`

2. `Whitelist.fs` — `module Phos.Core.Whitelist`:
   - `type Whitelist = { Users: Map<UserId, UserRole>; AllowedChats: Set<ChatId> }`
   - `type DenialReason = NotWhitelisted | ChatNotAllowed`
   - `type Decision = Allow of UserRole | Deny of DenialReason`
   - `val create: Map<UserId, UserRole> -> Set<ChatId> -> Whitelist`
   - `val authorize: Whitelist -> User -> Chat -> Decision`
     - Private: разрешён только whitelisted user (его роль); иначе `Deny NotWhitelisted`.
     - Group/Channel: разрешён только если `ChatId ∈ AllowedChats`; роль = роль из `Users` если есть, иначе `User`. Иначе `Deny ChatNotAllowed`.

3. `ToolPolicy.fs` — `module Phos.Core.ToolPolicy`:
   - `type ToolPolicy = Allow | Prompt | Deny`
   - `type Policy = { Default: ToolPolicy; Tools: Map<string, ToolPolicy>; AdminTools: Set<string> }`
   - `type Decision = { Allowed: bool; NeedsConfirmation: bool }`
   - `val resolve: Policy -> UserRole -> toolName: string -> Decision`
     - инструмент в `AdminTools` и роль не Owner/Admin → `Allowed=false`.
     - `Deny` → `Allowed=false`; `Prompt` → `Allowed=true, NeedsConfirmation=true`; `Allow` → `Allowed=true`.
     - неизвестный инструмент → `Default`.

4. `PromptBudget.fs` — `module Phos.Core.PromptBudget`:
   - `type Layer = BasePolicy | Persona | Skills | CoreMemory | Recall | State | CurrentJob`
   - `type LayerContent = { Layer: Layer; Text: string }`
   - `type Budget = { TotalTokens: int; PerLayer: Map<Layer, int>; TruncationOrder: Layer list }`
   - `val truncate: (string -> int) -> maxTokens: int -> text: string -> string` (детерминированное усечение по токенам)
   - `val build: (string -> int) -> Budget -> LayerContent list -> (Layer * string) list`
     - детерминированно: сначала усечь слои в порядке `TruncationOrder` (первые — усекаются раньше), до соблюдения `TotalTokens` и `PerLayer`.
     - слои, отсутствующие в контенте, пропускаются; порядок слоёв в результате = порядок контента.

5. `Chunker.fs` — `module Phos.Core.Chunker`:
   - `type EntityKind = Bold | Italic | Code | Pre | TextUrl | Mention | Hashtag | Unknown`
   - `type Entity = { Offset: int; Length: int; Kind: EntityKind }` (offsets в UTF-16 code units)
   - `type Chunk = { Text: string; Entities: Entity list; FenceOpened: bool; FenceClosed: bool }`
   - `val chunk: maxUnits: int -> text: string -> entities: Entity list -> Chunk list`
     - инварианты: каждый `Text` ≤ `maxUnits` UTF-16 code units; entity offsets/lengths валидны и rebased внутри чанка; entity, пересекающий границу чанка, разбивается на два (same kind, пересчитанные длины); ```-фенсы не разрезаются: если разрез внутри фенса — чанк закрывается ```` ``` ````, следующий открывается заново (`FenceClosed`/`FenceOpened`); без фенсов конкатенация `Text` == исходный `text`.

6. `Rrf.fs` — `module Phos.Core.Rrf`:
   - `val score: k: float -> ranks: seq<seq<'T>> -> Map<'T, float>`
     - `score(t) = Σ_lists 1/(k + rank)` (rank 1-based); монотонность: добавление списка не уменьшает score; более ранний rank даёт не меньший вклад.

7. `SchedulePolicy.fs` — `module Phos.Core.SchedulePolicy`:
   - `type CatchupPolicy = SkipMissed | CatchUpOnce`
   - `type Policy = { Catchup: CatchupPolicy; MaxCatchUp: int }`
   - `type Occurrence = { ScheduledFor: DateTimeOffset }`
   - `val occurrenceKey: jobId: int64 -> ScheduledFor: DateTimeOffset -> string`
   - `val isDue: Policy -> now: DateTimeOffset -> lastRun: DateTimeOffset option -> scheduled: DateTimeOffset -> bool`
     - будущее → false; `lastRun >= scheduled` → false; пропущенное (scheduled < now, lastRun отсутствует/меньше) → по `Catchup`: SkipMissed → false (кроме... см. ниже), CatchUpOnce → true максимум один раз.
   - `val resolveLocal: tz: TimeZoneInfo -> local: DateTime -> DateTimeOffset option` (DST: invalid time → None; ambiguous → фиксированный выбор, документировать)
   - `val nextOccurrences: cron: Cronos.CronExpression -> tz: TimeZoneInfo -> after: DateTimeOffset -> count: int -> DateTimeOffset list`
   - FsCheck: DST spring/fall fixtures через `nextOccurrences`/`resolveLocal`; `isDue` с SkipMissed/CatchUpOnce; `occurrenceKey` уникальность.

8. `InboxStateMachine.fs` — `module Phos.Core.InboxStateMachine`:
   - `type Status = Pending | Claimed | Running | Completed | Failed | DeadLetter`
   - `type Command = { Id: int64; Origin: Origin; Status: Status; Attempts: int; MaxAttempts: int; LeaseUntil: DateTimeOffset option; HeartbeatAt: DateTimeOffset option }`
   - `type Event = Claim of DateTimeOffset | Start | Complete | Fail | LeaseExpired of DateTimeOffset | Retry | DeadLetter`
   - `val apply: Command -> Event -> Result<Command, string>`
     - Pending --Claim(now)--> Claimed (attempts+1, lease=now+lease — lease duration передаётся как параметр события? проще: `Claim of DateTimeOffset * DateTimeOffset` (now, leaseUntil))
     - Claimed --Start--> Running
     - Running --Complete--> Completed
     - Running --Fail--> Failed; если attempts ≥ MaxAttempts → DeadLetter
     - Claimed/Running --LeaseExpired--> Pending (сброс lease)
     - Failed --Retry--> Pending (если attempts < MaxAttempts)
     - Failed --DeadLetter--> DeadLetter
   - `val canClaim: Command -> bool`, `val isLeaseExpired: now: DateTimeOffset -> Command -> bool`, `val shouldDeadLetter: Command -> bool`

9. `OutboxStateMachine.fs` — `module Phos.Core.OutboxStateMachine`:
   - `type Status = Pending | Sending | Sent | Failed`
   - `type Entry = { Id: int64; CommandId: int64; ChunkIndex: int; RandomId: int64; Status: Status; Attempts: int; MaxAttempts: int; RemoteMessageId: int64 option }`
   - `type Event = BeginSend | Sent of int64 | Fail`
   - `val apply: Entry -> Event -> Result<Entry, string>` (Pending --BeginSend--> Sending; Sending --Sent(remoteId)--> Sent; Sending --Fail--> Failed (attempts+1; max → DeadLetter не существует — Failed терминал+retry); Failed --BeginSend--> Sending если canRetry)
   - `val canSend: Entry -> bool` (Pending или Failed+canRetry), `val canRetry: Entry -> bool` (Failed && attempts < MaxAttempts)
   - Инвариант: `RandomId` неизменяем при retry (поле immutable по конструкции; тест: retry не создаёт вторую запись и не меняет random_id).

Тесты (`tests/Phos.Tests/CoreTests.fs`, добавить в `Phos.Tests.fsproj`):
- FsCheck chunk invariants (maxUnits, entity bounds, reassembly, fence balance);
- FsCheck state machine transitions (valid/invalid, retry/dead-letter, lease expiry);
- FsCheck RRF monotonicity;
- FsCheck whitelist/tool authorization (roles, groups, admin tools, unknown tool → Default);
- cron/DST fixtures (spring gap, fall ambiguous, CatchUpOnce один раз, SkipMissed);
- prompt budget: детерминизм, соблюдение TotalTokens/PerLayer.

Acceptance: Core без I/O; branch coverage Core ≥ 90% (проверяется `scripts/ci.sh`).

---

## Phase 2 — Encrypted storage и durable queue (`src/Phos.Storage`)

Пакеты: `Microsoft.Data.Sqlite` 10.0.12 (pin в `Directory.Packages.props`). Проект `src/Phos.Storage/Phos.Storage.fsproj`, ссылается на `Phos.Core`. Добавить в `Phos.sln`.

Файлы:

1. `Schema.fs` — DDL миграций (версии 1..N, в транзакции каждая):
   - `schema_version(version INTEGER NOT NULL, applied_at INTEGER NOT NULL)`
   - `command_inbox(id INTEGER PRIMARY KEY AUTOINCREMENT, origin TEXT NOT NULL, external_key TEXT UNIQUE, user_id INTEGER NOT NULL, chat_id INTEGER NOT NULL, payload TEXT NOT NULL, priority INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT 'pending', lease_until INTEGER NULL, heartbeat_at INTEGER NULL, attempts INTEGER NOT NULL DEFAULT 0, max_attempts INTEGER NOT NULL DEFAULT 5, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL)`
   - `message_outbox(id INTEGER PRIMARY KEY AUTOINCREMENT, command_id INTEGER NOT NULL, chunk_index INTEGER NOT NULL, chat_id INTEGER NOT NULL, random_id INTEGER NOT NULL UNIQUE, payload TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'pending', remote_message_id INTEGER NULL, attempts INTEGER NOT NULL DEFAULT 0, max_attempts INTEGER NOT NULL DEFAULT 5, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, UNIQUE(command_id, chunk_index))`
   - `mcp_servers(id INTEGER PRIMARY KEY AUTOINCREMENT, scope_key TEXT NOT NULL, name TEXT NOT NULL, config_json TEXT NOT NULL, enabled INTEGER NOT NULL DEFAULT 1, added_by INTEGER NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, UNIQUE(scope_key, name))`
   - `mcp_tokens(server_id INTEGER NOT NULL REFERENCES mcp_servers(id), user_id INTEGER NOT NULL, scope TEXT NOT NULL, ciphertext BLOB NOT NULL, nonce BLOB NOT NULL, expires_at INTEGER NULL, PRIMARY KEY(server_id, user_id, scope))`
   - timestamps: INTEGER unix seconds (UTC).
   - `type Migration = { Version: int; Name: string; Sql: string }`, `val migrate: StorageExecutor -> Migration list -> Task<int>` (идемпотентно, возвращает число применённых).

2. `StorageExecutor.fs`:
   - `type StorageOptions = { DatabasePath: string; BusyTimeout: TimeSpan; ReadPoolSize: int; CheckpointEvery: int }`
   - `type StorageExecutor` — единственный writer: `WriteAsync: (SqliteConnection -> 'T) -> Task<'T>` (сериализовано, SemaphoreSlim(1)); `ReadAsync: (SqliteConnection -> 'T) -> Task<'T>` (bounded pool `ReadPoolSize`); соединения между потоками не разделяются.
   - Открытие: `PRAGMA journal_mode=WAL`, `PRAGMA busy_timeout=<ms>`, `PRAGMA foreign_keys=ON`.
   - Checkpoint policy: после каждых `CheckpointEvery` write-операций — `PRAGMA wal_checkpoint(TRUNCATE)`; `CheckpointNow()` для тестов.
   - `IDisposable`/`IAsyncDisposable`.
   - `openExecutor: StorageOptions -> StorageExecutor` + тестовая фабрика `openTemp` (temp file, WAL).

3. `CommandInbox.fs` — `ICommandInbox` (интерфейс + SQLite-реализация):
   - `type CommandEnvelope = { Origin: Origin; ExternalKey: string option; UserId: UserId; ChatId: ChatId; Payload: string; Priority: int }`
   - `type Lease = { Until: DateTimeOffset; HeartbeatAt: DateTimeOffset }`
   - `type Command = { Id: int64; Envelope: CommandEnvelope; Status: InboxStateMachine.Status; Attempts: int; MaxAttempts: int; LeaseUntil: DateTimeOffset option; HeartbeatAt: DateTimeOffset option }`
   - методы: `Insert` (UNIQUE external_key → конфликт = уже есть, вернуть существующий id/исключение — определить), `ClaimNextForChat: ChatId -> Lease -> Task<Command option>` (только Pending; атомарно UPDATE ... WHERE id = (SELECT ...) ), `ClaimById`, `MarkStarted`, `MarkCompleted`, `MarkFailed` (attempts+1 → failed/dead_letter по max_attempts), `Retry`, `Heartbeat`, `ExpireLeases: DateTimeOffset -> Task<int>`, `CountPending`, `CountDeadLetter`.

4. `MessageOutbox.fs` — `IMessageOutbox` + реализация:
   - `type OutboxEnvelope = { CommandId: int64; ChunkIndex: int; ChatId: ChatId; RandomId: int64; Payload: string }`
   - `type Entry = { Id: int64; Envelope: OutboxEnvelope; Status: OutboxStateMachine.Status; Attempts: int; MaxAttempts: int; RemoteMessageId: int64 option }`
   - методы: `Insert` (UNIQUE random_id; UNIQUE(command_id, chunk_index)), `BeginSend`, `MarkSent: id -> remoteMessageId -> unit`, `MarkFailed`, `Retry` (не меняет random_id, не создаёт дубль), `NextPending`, `GetByRandomId` (идемпотентность), `CountPending`.

5. `TokenCrypto.fs`:
   - `type ITokenCipher = abstract Encrypt: byte[] -> byte[] * byte[] (*nonce, ciphertext*); abstract Decrypt: nonce: byte[] -> ciphertext: byte[] -> byte[]`
   - `val aesGcm: masterKey: byte[] -> ITokenCipher` (AES-256-GCM, 12-byte random nonce, AAD = scope)
   - `type ITokenRepository = abstract Upsert: serverId: int64 -> UserId -> scope: string -> token: byte[] -> expiresAt: DateTimeOffset option -> Task<unit>; abstract Get: serverId: int64 -> UserId -> scope: string -> Task<byte[] option>`
   - `type IMasterKeyProvider = abstract GetKey: unit -> byte[]` (тест: фиксированный ключ; production — systemd credential, позже).

6. `Repositories.fs` — общие фабрики/DI-хелперы (по вкусу).

Тесты (`tests/Phos.Tests/StorageTests.fs`), temp-file SQLite с WAL:
- миграции: fresh → latest; повторный запуск идемпотентен; upgrade replay;
- concurrency: N параллельных `Insert` через `WriteAsync` → все закоммичены, без потерь; busy timeout ограничивает ожидание (admission timeout ~2s);
- crash boundaries: транзакция без commit (dispose) → rollback, нет частичного состояния; committed данные переживают закрытие/переоткрытие (WAL replay);
- leases: два конкурентных claim одной команды → ровно один побеждает; expiry → обратно в pending; attempts ≥ max → dead-letter;
- restart replay: pending после reopen остаётся claimable;
- outbox: retry сохраняет random_id; duplicate insert с тем же random_id не создаёт вторую запись;
- tokens: encrypt/decrypt roundtrip; сырые байты DB не содержат master key / plaintext (сканировать файл).

Acceptance: durable command переживает process kill (симуляция: committed → закрыть без clean shutdown → переоткрыть → команда claimable); ключи отсутствуют в DB и логах.

---

## Phase 3 — Async pipeline (`src/Phos.Pipeline`)

Проект `src/Phos.Pipeline/Phos.Pipeline.fsproj`, ссылается на `Phos.Core`, `Phos.Storage`. Добавить в `Phos.sln`. Пакеты: `System.Threading.Channels` входит в runtime (без пакета).

Файлы:

1. `Clock.fs` — `type IClock = abstract UtcNow: DateTimeOffset`; `SystemClock` + `MutableClock` (для тестов).

2. `WakeChannel.fs` — bounded coalescing channel (`Channel<ChatId>`, capacity `WakeCapacity`):
   - `type WakeChannel (capacity: int)` — `TryWake: ChatId -> bool`, `ReadAllAsync: unit -> IAsyncEnumerable<ChatId>` (или `TryRead`), `Count`.
   - Коалесценция: chat id в канале не дублируется; при заполнении сигнал может быть потерян (это допустимо — durable scan fallback), `TryWake` возвращает false.

3. `ResourceSemaphores.fs`:
   - `type WorkKind = Llm | Stt | Mcp`
   - `type ResourceSemaphores = { Llm: SemaphoreSlim; Stt: SemaphoreSlim; Mcp: SemaphoreSlim }` — отдельные, никакого общего semaphore; фабрика `create: Llm: int -> Stt: int -> Mcp: int -> ResourceSemaphores`.

4. `ControlRegistry.fs`:
   - `type ControlRegistry = ...` — `Register: ChatId -> CancellationTokenSource`, `Cancel: ChatId -> bool`, `CancelAll`, `TryGet`; concurrent (`ConcurrentDictionary`).

5. `ChatCoordinator.fs`:
   - `MailboxProcessor<ChatControl>`, `ChatControl = Start of ClaimedCommand | Cancel | WorkDone of WorkOutcome`
   - владеет chat state (idle/running); при `Start` — регистрирует CTS, берёт semaphore по `WorkKind`, запускает child task `IWorkExecutor.Execute` и сразу возвращается в loop (нет ожидания внутри обработчика); `WorkDone` — освобождает semaphore, снимает CTS, обновляет состояние.

6. `Dispatcher.fs`:
   - fair round-robin по chat: `TickAsync: unit -> Task<int>` — выбирает следующий chat с pending (через `ICommandInbox`), не допускает >1 active command на chat; round-robin pointer хранится в состоянии; noisy chat не может занять очередь (после каждого claim pointer двигается).

7. `Pipeline.fs`:
   - `type PipelineOptions = { WakeCapacity: int; AdmissionTimeout: TimeSpan; LeaseDuration: TimeSpan; Limits: ResourceLimits; MaxAttempts: int; Quota: Quota }`
   - `type Pipeline (clock: IClock, inbox: ICommandInbox, executor: IWorkExecutor, semaphores: ResourceSemaphores, options)`
   - `AdmitAsync: CommandEnvelope -> Task<AdmitResult>` — короткая транзакция dedupe+INSERT (с лимитом времени `AdmissionTimeout`; при неудаче → `AdmitResult.Rejected`), затем `TryWake`; никогда не ждёт LLM/STT/MCP.
   - `StopAsync: ChatId -> Task<unit>` — немедленно `ControlRegistry.Cancel`, не через очередь.
   - `StartAsync` / `ShutdownAsync` (graceful: stop admission → cancel active → release leases).
   - `RunAsync`/`TickAsync` loop: читает wake, `ExpireLeases`, `TickAsync`, dead-letter (max attempts через state machine).
   - `IWorkExecutor` интерфейс: `abstract Execute: WorkKind -> ChatId -> CancellationToken -> Task<WorkOutcome>`; `type WorkOutcome = Completed | Cancelled | Failed` (тесты: fake с TCS для блокировки и детекта отмены).

Тесты (`tests/Phos.Tests/PipelineTests.fs`), deterministic fake clock/LLM:
- handler принимает второй chat, пока первый fake LLM заблокирован (параллельность между chats);
- строгий порядок внутри chat, параллельность между chats;
- `/stop` отменяет текущую задачу до завершения (fake executor наблюдает CancellationToken);
- заполненный wake-channel не теряет durable command (после `TryWake=false` — сканирование подхватывает);
- fairness: noisy chat не блокирует другой (round-robin);
- crash после inbox commit / после claim → restart replay (переоткрытие executor, команда обрабатывается);
- quota: превышение per-user depth/bytes → Rejected с retry-after; dead-letter после max attempts;
- 10k admitted fake commands: все обработаны, `CurrentQueueLength` координаторов/канала остаётся ограниченным (нет unbounded mailbox growth);
- semaphores: блокировка STT не блокирует LLM.

Acceptance: blocked chat A не задерживает chat B; `/stop` отменяет A; 10k admitted fake commands не создают unbounded mailbox growth; branch coverage Pipeline ≥ 90%.

---

## Порядок работ

1. Phase 1 (агент A): Core + CoreTests → `scripts/ci.sh` EXIT 0.
2. Phase 2 (агент B): Storage + StorageTests → `scripts/ci.sh` EXIT 0.
3. Phase 3 (агент C): Pipeline + PipelineTests → `scripts/ci.sh` EXIT 0.
4. Владелец сессии: финальный `scripts/ci.sh`, коммиты в `feat/phases-1-3`, обновление `docs/phase0-summary.md`-стиля (новый `docs/phase-1-3-summary.md`).

Файлы-владельцы: агент A — только `src/Phos.Core/**`, `tests/Phos.Tests/CoreTests.fs`, `Directory.Packages.props` (Cronos), `Phos.sln` не трогает. Агент B — `src/Phos.Storage/**`, `tests/Phos.Tests/StorageTests.fs`, `Phos.sln` (add Storage), `Directory.Packages.props` (Sqlite). Агент C — `src/Phos.Pipeline/**`, `tests/Phos.Tests/PipelineTests.fs`, `Phos.sln` (add Pipeline). Общие файлы (`Phos.Tests.fsproj`) правит только текущий агент (агенты последовательны).
