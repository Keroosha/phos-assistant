# Phase 6 — Шедулер, управляемый промптом (персистентный loop)

Дата актуализации: 2026-09-18. Статус: **реализовано**.

Phase 6 реализует prompt-driven расписания в `Phos.Core`, `Phos.Storage`, `Phos.Scheduler` и `Phos.Omp`. Архитектурное решение сохраняется: нужен только loop-режим, без goal-режима; расписания переживают рестарты и выключаются явным действием пользователя.

## 0. Зачем

Пользователь пишет в Telegram естественным языком: «напоминай мне каждый день в 09:00» или «проверяй деплой каждые 5 минут». Агент управляет расписанием через host tools, без слэш-команд (`/stop` по-прежнему останавливает текущий ход, но не удаляет расписание).

Задание и его состояние живут в SQLite. После рестарта scheduler продолжает работу, а уже поставленные команды доставляются через durable `command_inbox` с at-least-once семантикой. Периодический loop продолжается до `schedule_pause`/`schedule_remove`; одноразовое задание завершается после единственной постановки команды.

## 1. Виды расписаний и жизненный цикл

`schedule_add` принимает ровно один режим:

| Поле | Режим | Семантика |
|---|---|---|
| `cron_expr` | recurring | Повтор по cron. |
| `interval_seconds` | recurring | Повтор через заданное число секунд. |
| `run_at` | one-shot | Один календарный запуск в будущем, с точным временем. |
| `after_seconds` | one-shot | Один запуск через заданное число секунд после подтверждения. |

Проверка выполняется до записи: нельзя смешивать режимы, `prompt` обязателен, cron должен разбираться, интервал не меньше квоты, `after_seconds >= 1`, а `run_at` должен указывать будущее.

Новый job имеет `status=pending` и не запускается до явного подтверждения. Подтверждение переводит его в `active` и вычисляет `next_run`. Pending-задание можно отменить; просроченный черновик становится `expired`. Active можно приостановить и возобновить. One-shot после атомарной постановки ровно одной команды получает `completed` и больше не срабатывает.

## 2. Host tools

Инструменты регистрируются через `set_host_tools`:

| Tool | Параметры | Результат |
|---|---|---|
| `schedule_add` | `prompt` (обязателен); ровно одно из `run_at`, `cron_expr`, `interval_seconds`, `after_seconds`; `timezone`, `catch_up`, `chat_id` — необязательны | Создаёт `pending`. Для recurring возвращает ближайшие 5 срабатываний в UTC и локальном времени; для one-shot — единственное рассчитанное время и ожидание подтверждения. |
| `schedule_confirm` | `job_id` | `pending → active`; разрешён только из хода с Telegram/user origin. |
| `schedule_cancel` | `job_id` | Отменяет `pending`. |
| `schedule_list` | — | Jobs пользователя, status и `next_run`; one-shot помечается как разовый. |
| `schedule_pause` / `schedule_resume` | `job_id` | `active → paused` / `paused → active`. |
| `schedule_remove` | `job_id` | Удаляет job и связанные `schedule_runs`. |
| `schedule_run_now` | `job_id` | Для active выставляет `next_run` на сейчас; запуск произойдёт на ближайшем скане. |

Если `timezone` не передан, используется `TimeZoneInfo.Local.Id` процесса-хоста. `chat_id` можно передать явно, иначе берётся текущий чат. `catch_up` принимает `skip` или `once`, по умолчанию `skip`; для one-shot поздний запуск всё равно происходит один раз.

Исполнитель host tools кэширует результат по `toolCallId` в памяти. Для `schedule_add` дополнительная защита от повтора после рестарта — поиск по `origin_tool_call_id` и уникальный индекс в SQLite.

## 3. Модель данных и миграции

Текущая `schedule_jobs` содержит:

```text
user_id, chat_id, prompt
cron_expr NULL, interval_seconds NULL
run_at NULL, after_seconds NULL
timezone, catchup_policy
status: pending | active | paused | cancelled | expired | completed
next_run, last_run_at, last_error
origin_tool_call_id UNIQUE
created_at, updated_at
```

`run_at`, `next_run`, `last_run_at` и служебные timestamps хранятся как Unix seconds. Значение `run_at` нормализуется к UTC до записи. Режим расписания задаётся валидацией (в базе все четыре поля остаются nullable); поле `enabled` удалено, его заменяет `status`.

`schedule_runs(job_id, scheduled_for, status, command_id)` имеет логическую уникальность `(job_id, scheduled_for)`. Она не позволяет повторно заявить один occurrence; `command_id` связывает его с командой в `command_inbox`.

Актуальная схема получается последовательностью миграций 1–12:

- миграция 9 добавляет `chat_id`, `interval_seconds`, lifecycle/status, `origin_tool_call_id`, `last_run_at`, `last_error` и удаляет `enabled`;
- миграция 10 добавляет относительный one-shot `after_seconds`;
- миграция 11 делает `cron_expr` nullable для режимов без cron;
- миграция 12 добавляет nullable `run_at` для абсолютного one-shot (UTC Unix seconds).

## 4. Реализация runtime

`Phos.App` подключает `SchedulerService` из `Phos.Scheduler` вместе с OMP worker. Фоновый цикл с `TickSeconds=15` на каждом скане:

1. переводит pending jobs старше `PendingTtlHours=24` в `expired`;
2. повторяет `ClaimDueOccurrence`, пока есть due jobs;
3. после каждой успешной claim будит OMP worker.

`ClaimDueOccurrence` выполняет выбор due active job, постановку и обновление расписания одной SQLite-транзакцией. Команда получает `origin=schedule`, priority `10`, внешний ключ `sched:<job_id>:<scheduled_ticks>` и payload с префиксом `[по расписанию]`. Повторный тик защищён уникальностью `command_inbox.external_key` и `(job_id, scheduled_for)`.

Для recurring `isDue` учитывает `catch_up`: пропущенный occurrence либо пропускается (`skip`), либо выполняется один раз (`once`), после чего `next_run` сдвигается. Для `run_at` и `after_seconds` команда ставится один раз, job становится `completed`; даже опоздавший после рестарта one-shot не теряется и не повторяется.

Дальше команда идёт через существующий per-user serialized OMP pipeline и `--resume`. Telegram-команды имеют priority `0`, scheduled — `10`; `/stop` прерывает текущий ход, но не меняет состояние job.

## 5. Guard'ы и отказоустойчивость

Секция `Scheduler` в конфигурации (`SchedulerSettings`) имеет следующие значения по умолчанию:

- `MaxJobsPerUser = 20` — учитываются active jobs;
- `MinIntervalSeconds = 60` — минимальный `interval_seconds`;
- `MaxPromptLength = 2000`;
- `MaxFailedTicks = 3`;
- `TickSeconds = 15`;
- `PendingTtlHours = 24`.

Дополнительная валидация проверяет непустой prompt, известную IANA/system timezone, корректный cron, ровно один режим и будущий `run_at`. One-shot `after_seconds` должен быть положительным. Ошибка квоты или формата возвращается как `isError` и не создаёт job.

`MaxFailedTicks` относится к последовательным ошибкам **скана scheduler**, а не к отдельному job. После трёх ошибок polling delay увеличивается с `TickSeconds` до `2 * TickSeconds`; успешный скан сбрасывает счётчик. Ошибка базы не переводит jobs в `paused`: они остаются durable и будут повторно проверены.

Confirm-guard принимает `schedule_confirm` только из Telegram/user-origin хода и только для pending job. Поэтому scheduled-ход может создать pending-черновик, но не может сам его активировать. Status guards ограничивают cancel pending, pause active и resume paused.

## 6. Timezone и DST

Timezone хранится у каждого job. Без offset `run_at` интерпретируется как local wall-clock в указанной timezone; `Z` или явный offset обозначают instant и нормализуются к UTC. Без поля `timezone` используется локальная timezone процесса-хоста, а не фиксированная зона. Поддерживаются явные IANA timezone; неизвестная зона отклоняется.

`run_at` требует ISO-8601 даты **и точного времени** (например, `2026-10-02T09:00`). Ввод только даты (`2026-10-02`) отклоняется, чтобы агент запросил время, а не угадывал его. Невозможное offset-less local время в spring-forward gap отклоняется; неоднозначное fall-back время разрешается детерминированно стандартным (зимним) offset.

Для recurring cron используется Cronos: spring-forward gap сдвигается на существующее post-jump local time (например, 02:30 → 03:00), а fall-back даёт одно детерминированное срабатывание. В ответах время показывается одновременно в UTC и timezone job. Колонка `users.timezone` не является источником scheduling-настроек.

## 7. Acceptance реализации

- `schedule_add` принимает четыре взаимоисключающих режима; `run_at` требует будущую дату-время, date-only отклоняется с просьбой назвать точное время.
- One-shot `run_at` и `after_seconds` проходят `pending → active → completed`, ставят ровно одну inbox-команду и не повторяются после рестарта или позднего скана.
- Recurring cron/interval показывают ближайшие 5 occurrence, учитывают catch-up policy и продолжаются после рестарта.
- Confirm доступен только из Telegram/user-origin хода; scheduled-ход не может сам себя активировать.
- Атомарный claim и уникальные ключи не допускают duplicate tick/duplicate command.
- Pending TTL, квоты, cancel/pause/resume/remove и `schedule_run_now` работают через host tools.
- DST spring/fall, локальный default timezone, UTC-нормализация и миграция 12 покрыты core/storage сценариями.
- Ошибки scan увеличивают только polling delay по правилу `MaxFailedTicks`; jobs не ставятся на автопаузу.
- Scheduled prompt попадает в durable inbox с origin `schedule`, priority `10` и префиксом `[по расписанию]`, затем обрабатывается обычным serialized OMP pipeline.

## 8. Компоненты реализации

- **`Phos.Core`**: `ScheduleJob`/`ScheduleJobDraft`, статусы, валидация, parsing `run_at`, вычисление occurrence и DST policy.
- **`Phos.Storage`**: миграции 1–12, repository CRUD/status transitions, pending cleanup и атомарный `ClaimDueOccurrence` с inbox insert.
- **`Phos.Scheduler`**: hosted background loop, последовательный scan, wake worker и scan-failure backoff.
- **`Phos.Omp`**: регистрация и выполнение `schedule_*`, origin guard для confirm, idempotency cache и доставка prompt через inbox.
- **`Phos.App`**: binding/validation `Scheduler` settings и wiring scheduler с OMP worker.
- **Workspace**: host-managed блок scheduling-инструкций в `.omp/APPEND_SYSTEM.md` обновляется idempotently при каждом `Ensure`, включая уже существующие workspaces; пользовательский остальной persona-контент сохраняется.

## 9. Архитектурные решения и риски

- Явное подтверждение перед активацией — обязательная граница между предложением агента и side effect.
- SQLite job state плюс durable inbox дают restart-safe at-least-once путь; уникальные ключи ограничивают повтор одной occurrence.
- Очередь сериализует scheduled-промпты с обычным диалогом; priority `10` позволяет обработать расписание, не вводя второй pipeline.
- Бесконечность recurring loop — осознанный контракт: пользователь должен явно сказать «пауза» или «удали». Pending TTL и scan backoff защищают систему, но не заменяют пользовательское управление.
