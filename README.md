# phos-assistant

**phos** — персональный ассистент с Telegram-фронтендом (F#, .NET 10). Сообщения
пользователей принимаются через Telegram (WTelegramClient), попадают в durable-очередь
`command_inbox` в SQLite и обрабатываются локальным агентом **OMP**
(`omp --mode rpc`, по одной сессии и workspace на пользователя); ответы возвращаются
через транзакционный outbox (`message_outbox`) с повторными попытками доставки.

## Что уже работает

- **Whitelist-роутинг**: личные чаты — только whitelisted-пользователи (роли
  `owner`/`admin`/`user`); группы/каналы — только из `Whitelist:AllowedChats`.
  Запрещённые апдейты не доходят до инбокса; дедупликация обновлений.
- **Диалог с агентом**: любой текст (включая фото и reply-контекст) уходит в OMP;
  ответ приходит markdown-чанками с entities; 👀-реакция на принятую команду,
  «печатает…» во время хода. `/stop` прерывает текущий ход агента.
- **Голос → текст (STT)**: локально, через ffmpeg → Silero VAD → GigaAM v3 CTC
  int8 (основная модель, русский), Whisper small int8 — fallback.
- **Шедулер (loop), управляемый промптом**: агент сам создаёт задания через
  host tools (`schedule_add` → подтверждение → `schedule_confirm`; также
  `schedule_list`/`schedule_pause`/`schedule_resume`/`schedule_remove`/`schedule_cancel`/
  `schedule_run_now`). Cron или интервал, таймзона per-job (IANA), задания
  персистентны в SQLite и переживают рестарт; остановка — только промптом.
- **Host tools агента**: `tg_send_message`, `tg_edit_message`, `stt_transcribe`,
  `schedule_*`; чтение `tg://` URI (например, голосовые сообщения как контекст).
- **Пер-юзер OMP-профиль и workspace**: профиль создаётся из `SourceProfile`
  (`config.yml`, `models.yml`, `.env`), workspace — `~/.phos/workspace/<uid>` с
  `APPEND_SYSTEM.md` (персона, по умолчанию `personas/phos.md`); простаивающие
  сессии завершаются по `IdleTimeoutMinutes` и возрождаются с `--resume`.
- **Локальные бэкапы** (см. ниже): снапшот SQLite + OMP-профиля + workspace,
  манифест с sha256, шифрование age, retention.

Не реализовано (не документировать как доступное): off-host отгрузка бэкапов,
автоматический restore поверх живых данных (restore — только ручной runbook,
см. `docs/phase7-backup.md` §8).

## Предварительные требования

- **.NET SDK 10** (пиннится через `global.json`);
- **omp** — `@oh-my-pi/pi-coding-agent` (ставится через bun: `curl -fsSL https://bun.sh/install | bash`, затем `npm install -g @oh-my-pi/pi-coding-agent`);
- **ffmpeg** (декодирование голосовых сообщений, путь — `Stt:FfmpegPath`);
- **age** (только если включены бэкапы `Backup:Enabled`).

## Настройка

1. Скопируйте пример конфигурации в `appsettings.json` (в корне репозитория,
   игнорируется git):

   ```bash
   cp appsettings.example.json appsettings.json
   ```

   Заполните `Whitelist:Users[0].Id` вашим Telegram user id (роль `owner`) и
   перечислите id групп/каналов в `Whitelist:AllowedChats`.

2. Задайте секреты через переменные окружения (не храните их в файле):

   | Переменная | Откуда |
   |---|---|
   | `PHOS_TELEGRAM__APIID` | `api_id` из [my.telegram.org](https://my.telegram.org) |
   | `PHOS_TELEGRAM__APIHASH` | `api_hash` из [my.telegram.org](https://my.telegram.org) |
   | `PHOS_TELEGRAM__BOTTOKEN` | токен бота от [@BotFather](https://t.me/BotFather) |

   Двойное подчёркивание `__` — разделитель секций `IConfiguration`:
   `PHOS_TELEGRAM__APIID` соответствует `Telegram:ApiId`.

3. Скачайте STT-модели (идемпотентно, sha256-пины, файлы git-игнорируются):

   ```bash
   scripts/provision-stt.sh
   ```

   По умолчанию модели кладутся в `spikes/spike-03-stt/data` (как в
   `appsettings.example.json`); другой каталог — `scripts/provision-stt.sh --dir <path>`
   плюс соответствующий `Stt:ModelPath`/`Stt:TokensPath`/`Stt:VadModelPath`.
   Fallback-движок Whisper отключён, пока `Stt:WhisperDir` пуст.

## Первый запуск

```bash
dotnet run --project src/Phos.App
```

При старте: применяются миграции SQLite (`Storage:DatabasePath`, по умолчанию
`data/phos.db`), логин бота выполняется в фоне с flood-aware backoff (сетевой
сбой не валит хост), при `Stt:Enabled` модели проверяются по sha256 (ошибка
логируется, хост продолжает работу), создаётся/проверяется OMP-профиль
(`Omp:Profile` из `Omp:SourceProfile` — источник `config.yml`/`models.yml`/`.env`)
и workspace-каталог. Первый ответ появляется в Telegram после успешного логина.

## Конфигурация и её приоритет

Значения собираются в порядке возрастания приоритета:

1. `appsettings.json` (обязательны секции `Telegram`, `Storage`, `Whitelist`,
   `Stt`, `Omp`, `Scheduler`, `Backup` — валидация типов/границ на старте,
   ошибка конфигурации завершает процесс с кодом 1);
2. переменные окружения с префиксом `PHOS_`;
3. аргументы командной строки, например:

   ```bash
   dotnet run --project src/Phos.App -- --Storage:DatabasePath /tmp/phos.db
   ```

## Операционные заметки

### OMP: профиль и workspace

- Профиль живёт в `~/.omp/profiles/<Omp:Profile>/agent`, workspace —
  `Omp:WorkspaceRoot` (по умолчанию `~/.phos/workspace`), по каталогу на
  user id. Персона подключается через `Omp:PersonaFile` (по умолчанию пусто —
  используется встроенная дефолтная персона; для «Фос» укажите `personas/phos.md`).
- `Omp:Enabled: false` отключает воркер и шедулер (команды остаются в durable-очереди).
- Квоты очереди: `Omp:MaxQueuePerUser` (переполнение не теряет команды — они
  остаются в очереди), `Omp:IdleTimeoutMinutes`, `Omp:Tools`, `Omp:ApprovalMode`,
  `Omp:MaxTime`, `Omp:ReadyTimeoutSeconds`.

### STT: модели и ffmpeg

- Основной путь: ffmpeg-декод (whitelist кодеков, лимиты `Stt:MaxBytes`,
  `Stt:MaxDurationSeconds`) → Silero VAD → GigaAM; при отсутствии речи — пусто.
- `Stt:NumThreads`, `Stt:MaxConcurrentStt` (по умолчанию 4 и 1 — по данным
  бенчмарка, `spikes/spike-03-stt-benchmark.md`), `Stt:ModelSha256` — pin чек-суммы.
- Не найденный ffmpeg или модель не валят хост: ошибки транскрипции приходят
  пользователю как `⚠️ …`.

### Шедулер

- Квоты в секции `Scheduler`: `MaxJobsPerUser` (20), `MinIntervalSeconds` (60),
  `MaxPromptLength` (2000), `MaxFailedTicks` (3 — после трёх неудачных тиков
  подряд задание ставится на паузу), `TickSeconds` (15), `PendingTtlHours` (24 —
  неподтверждённые черновики протухают).
- Подтверждение (`schedule_confirm`) возможно только из хода, инициированного
  пользователем — scheduled-ход не может активировать задание (защита от
  саморепликации). Cron — через Cronos, поведение DST покрыто тестами.

### Бэкапы (локальные, age)

- При `Backup:Enabled`: прогон при старте + далее по `Backup:Interval`
  (TimeSpan, ≤ 30 дней). Каждый прогон: `VACUUM INTO` для всех SQLite
  (хост-БД, OMP `agent.db`/`models.db`, банки памяти mnemopi), копии сессий
  (JSONL с retry), конфигов профиля, блобов и workspace → `manifest.json`
  (sha256 + размеры) → `tar.gz` → шифрование `age -R <Backup:AgeRecipient>` →
  атомарная публикация `phos-backup-<timestamp>.age` в `Backup:Directory` →
  retention до `Backup:RetainCount` последних архивов + запись в таблицу
  `backup_log`.
- Секреты (bot token, `api_hash`, `.env` профиля, `wtelegram-bot.session`) в
  архив **не входят**. Файл `AgeRecipient` (публичный ключ, `age-keygen -y`)
  хранится вне архива; его отсутствие не валит хост, а фейлит конкретный прогон.
- Restore автоматический не реализован — только ручной runbook
  (`docs/phase7-backup.md` §8) и disposable-дрилл в интеграционных тестах.

## Сборка, проверки, CI

```bash
dotnet build Phos.sln -c Release                 # сборка (0 warnings — гейт)
dotnet test Phos.sln -c Release --no-build       # unit + integration тесты
scripts/ci.sh                                    # полный гейт: tool restore, build,
                                                 # fantomas --check, FSharpLint, тесты
                                                 # с покрытием + scripts/coverage-gate.py
```

CI (`.github/workflows/ci.yml`) на каждый push/PR: ставит .NET из `global.json`,
устанавливает omp (`@oh-my-pi/pi-coding-agent`), провижинит STT-модели и
запускает `scripts/ci.sh`. Интеграционные тесты поднимают реальный `omp --mode
rpc` с изолированным временным профилем и fake-LLM (`tests/Phos.IntegrationTests`).
