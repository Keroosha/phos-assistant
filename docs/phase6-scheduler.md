# Phase 6 — Шедулер, управляемый промптом (персистентный loop)

Дата: 2026-09-14. Статус: **дизайн, без кода** (решение владельца). Дополняет `research-plan.md` §2.6 и Phase 6.

## 0. Зачем

Владелец: «шедулер Фос должна выставляться по промпту, что-то типа loop из Claude Code, только персистентно». Уточнение владельца: **goal-режим не нужен совсем — только loop**, и он должен переживать рестарты, пока промпт его не отключит.

- Пользователь пишет в Telegram естественным языком: «напоминай мне каждый день в 09:00…», «проверяй деплой каждые 5 минут и чини».
- Агент (OMP) распознаёт намерение и **сам** управляет расписанием через host tools — никаких слэш-команд (`/schedule_add` из старого плана **отменяется**; команды уже убраны, остался только `/stop`).
- Персистентность: задания живут в SQLite (`schedule_jobs`/`schedule_runs` уже в схеме), переживают рестарт бота/хоста; **loop работает бессрочно, пока пользователь не отключит его промптом** («останови/удали напоминание» → агент вызывает `schedule_pause`/`schedule_remove`). В отличие от Claude Code `/loop` (session-scoped, fixed-интервалы умирают через 7 дней) — у нас durable: рестарт → `--resume` → цикл продолжается.

## 1. Loop (зеркало Claude Code `/loop`, но персистентный)

| | Claude Code `/loop` | phos |
|---|---|---|
| Механика | перезапуск промпта по интервалу (фикс. или динамический) | задание: cron или интервал → каждый тик кладёт промпт в сессию пользователя |
| Жизненный цикл | session-scoped; fixed-интервалы expire через 7 дней | **бессрочный**; завершается только промптом пользователя (`schedule_pause`/`schedule_remove` через агента) |
| Персистентность | нет (нужен запущенный CC) | **да**: DB + `--resume`, at-least-once по инбоксу |

## 2. Host tools (агентские, через `set_host_tools`)

Все — идемпотентны по `toolCallId` (кэш, как у существующих), мутирующие — с side-effect-защитой.

| Tool | Параметры | Результат |
|---|---|---|
| `schedule_add` | `prompt` (req), `cron_expr` \| `interval_seconds` (ровно одно), `timezone` (IANA, default UTC), `catch_up` (skip\|once, default skip), `chat_id` (default текущий) | `job_id`, `status=pending`, **следующие 5 срабатываний UTC+local** |
| `schedule_confirm` | `job_id` | `status=active` (только из user-хода — см. §5) |
| `schedule_cancel` | `job_id` | `status=cancelled` (отказ от pending) |
| `schedule_list` | — | задания пользователя + `status`, `next_run`, `last_run_at` |
| `schedule_pause` / `schedule_resume` | `job_id` | `paused` / `active` — управление циклом по промпту |
| `schedule_remove` | `job_id` | удаление + отмена `schedule_runs` |
| `schedule_run_now` | `job_id` | немедленный тик (ручной запуск, тесты) |

Отключение цикла — только через промпт: пользователь говорит «отключи/останови/удали» → агент вызывает `schedule_pause`/`schedule_remove`. Никаких автоматических стопов (нет капов ходов, дедлайнов, 7-дневных протуханий).

## 3. Модель данных (миграция 9; существующие таблицы расширяются)

`schedule_jobs` — добавляется:

```sql
chat_id            INTEGER NOT NULL                        -- куда доставлять (в плане отсутствовало!)
interval_seconds   INTEGER NULL                            -- sugar: ровно одно с cron_expr
status             TEXT NOT NULL DEFAULT 'pending'         -- pending|active|paused|cancelled|expired
origin_tool_call_id TEXT NULL UNIQUE                        -- restart-безопасная идемпотентность schedule_add
last_run_at        TEXT NULL
last_error         TEXT NULL
```

`enabled` (bool) поглощается `status`; `next_run`, `cron_expr`, `timezone`, `catchup_policy`, `prompt`, `user_id` — уже есть. **Никаких goal-полей** (режим один — loop).

`schedule_runs(job_id, scheduled_for, status, command_id, PK(job_id, scheduled_for))` — без изменений: гарантия «один occurrence → одна команда».

## 4. Рантайм (новый проект `src/Phos.Scheduler`)

- Фоновый тик (`TickSeconds`, default 15 с) + wake при вставке задания/подтверждении.
- **Тик loop**: `isDue(policy, now, last_run_at, next_run)` (уже в `SchedulePolicy`) → транзакция: `UPDATE next_run = nextOccurrences(...)[0], last_run_at = now`; `INSERT schedule_runs(job_id, scheduled_for, 'claimed')`; `INSERT command_inbox(origin=Schedule, user_id, chat_id, payload=prompt, priority=10)`. COMMIT.
- Доставка — существующий пайплайн: команда `origin=Schedule` попадает в очередь пользователя, `OmpWorker` уже всё умеет (одна сессия на пользователя, serialized turns, `--resume` после рестарта).
- Промпт scheduled-хода получает префикс `[по расписанию]` — агент видит контекст.
- Приоритет: scheduled-команды `priority=10`, пользовательские `0` — очередь сначала разбирает пользователя.
- `/stop` — по-прежнему abort текущего хода (цикл не трогается); остановка цикла — через промпт → `schedule_pause`/`schedule_remove`.

## 5. Guard'ы

- **Квоты — через конфиг** (секция `Scheduler`, `[<CLIMutable>] SchedulerSettings` в `Phos.App/Config.fs` + `appsettings.example.json`, по паттерну существующих секций). Дефолты:
  - `MaxJobsPerUser = 20` — max активных заданий на пользователя;
  - `MinIntervalSeconds = 60` — минимальный интервал/период cron;
  - `MaxPromptLength = 2000` — длина промпта задания;
  - `MaxFailedTicks = 3` — столько неудачных тиков подряд → `status=paused`, `last_error`, уведомление пользователю (без автоповтора);
  - `TickSeconds = 15` — период скана due-заданий;
  - `PendingTtlHours = 24` — TTL непринятых `pending`-черновиков → `expired` (cleanup).
  Нарушение квоты → `isError` с понятным текстом. Квоты не ограничивают время жизни цикла.
- **Confirm-флоу**: `schedule_add` всегда создаёт `pending` + показывает 5 ближайших; агент пересказывает пользователю и спрашивает подтверждение; `schedule_confirm` разрешён **только из user-хода** (OmpWorker держит per-user `CurrentTurnOrigin: Telegram|Schedule`, выставляется при старте хода). Саморепликация запрещена по построению: из scheduled-хода агент может создать `pending`, но активировать может только ход, инициированный человеком.
- **Идемпотентность**: `origin_tool_call_id` UNIQUE — повторный `schedule_add` после рестарта не дублирует задание; `schedule_runs` PK — повторный тик не дублирует команду.

## 6. DST и таймзоны

`SchedulePolicy.resolveLocal`/`nextOccurrences` уже решают spring-forward gap (invalid → skip) и fall-back ambiguity (детерминированный winter offset). Таймзона — per-job (IANA), default UTC; агент при неясности спрашивает пользователя. Колонка `users.timezone` больше не пишется (команды удалены) — источник истины таймзона задания.

## 7. Acceptance (расширение из research-plan Phase 6)

- DST spring/fall fixtures (расширить существующие тесты SchedulePolicy на scheduler);
- рестарт: active/paused переживают; in-flight ход — at-least-once через inbox lease; **цикл продолжает тикать после рестарта**;
- duplicate tick → ровно одна команда (`schedule_runs` UNIQUE);
- квоты и confirm: pending→active только через `schedule_confirm` из user-хода; pending протухает за 24 ч; отказ/удаление — `schedule_cancel`/`schedule_remove`;
- prompt-driven E2E: «напоминай каждый день в 09:00» → агент `schedule_add` → confirm → активен → тик доставляет промпт → агент отвечает в чат;
- остановка по промпту: «отключи напоминание» → `schedule_pause`/`schedule_remove` → тиков больше нет;
- саморепликация: scheduled-ход не может активировать новое задание.

## 8. Пофазная разбивка (TDD, отдельные саб-агенты + проверка по плану)

- **6a Core**: модель задания + валидация квот — чистое, тесты.
- **6b Storage**: миграция 9 + репозиторий заданий (CRUD, claim, confirm).
- **6c Scheduler**: тик-цикл, транзакция claim, enqueue; тесты рестарта/дубликата/DST.
- **6d Host tools**: `schedule_*` executor + per-turn origin + confirm/guard'ы + конфиг-секция `Scheduler` (квоты, тик, TTL) с валидацией как у существующих секций.
- **6e E2E**: сквозной сценарий «по промпту» (add→confirm→tick→reply→stop) + интеграционные тесты.

## 9. Риски

- Агент обязан спрашивать подтверждение — прописать правило в `APPEND_SYSTEM.md` (persona) + guard на `schedule_confirm` (нельзя обойти промптом).
- Цикл бессрочный: пользователь должен уметь остановить («отключи/удали») — это часть промпт-контракта; без этого цикл будет тикать вечно (по замыслу).
- Scheduled-промпты вперемешку с диалогом — сериализация очередью уже есть; приоритет 10 у scheduled.
