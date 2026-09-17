# Phase 7 — Backups: дизайн

Дата: 2026-09-17. Основание: `docs/research-plan.md` §2.7 (Backup) + Phase 7.

## 0. Решения владельца (2026-09-17)

1. **Только локальный архив.** Off-host копия (rclone/rsync/S3) в v1 не делается — отложено.
2. **age установлен** (`age-keygen` в комплекте). Шифрование через age CLI, `age -R <recipients-file>`, без unattended `age -p`.
3. **Версия OMP не пинится** (установлена 18.2.4, план упоминал 18.1.19). Версия всё равно **записывается в манифест** — информационно, для диагностики restore.
4. **Мусор сносится**: устаревший `data/phos.db`, тестовые `-tmp-ompcheck`/`ompcheck-*` в профиле.

## 1. Цель и объём

Периодический (по `TimeSpan`-интервалу) локальный бэкап:
- консистентный снапшот SQLite хоста (`dev/phos.db`);
- снапшот OMP-данных: профиль `phos` (конфиг, модели, памяти, сессии, блобы) + per-user workspace;
- манифест с checksum'ами; шифрование age; атомарная публикация; запись в `backup_log`; локальная retention.

**Не входит (v1):** off-host отгрузка, автоматический restore поверх живых данных (восстановление — документированный runbook, см. §8), секреты (см. §4).

## 2. Инвентарь данных (проверено на хосте 2026-09-17)

### 2.1. Что бэкапится

| Компонент | Путь | Размер | Способ снапшота |
|---|---|---|---|
| Хост SQLite | `dev/phos.db` | 2.1 МБ | `VACUUM INTO` |
| OMP agent.db (креды MCP, реестр сессий) | `~/.omp/profiles/phos/agent/agent.db` | 160 КБ | `VACUUM INTO` |
| OMP models.db | `~/.omp/profiles/phos/agent/models.db` | 1.7 МБ | `VACUUM INTO` |
| Банки памяти mnemopi (per-workspace) | `~/.omp/profiles/phos/agent/memories/mnemopi/banks/*/mnemopi.db` | ~640 КБ каждый | `VACUUM INTO` каждый |
| Сессии (JSONL, per-workspace) | `~/.omp/profiles/phos/agent/sessions/<ws>/*.jsonl` | 188 КБ | копия + retry при изменении |
| Конфиг профиля | `agent/config.yml`, `agent/models.yml` | 1.7 КБ | копия |
| Блобы (content-addressed) | `agent/blobs/*` | 896 КБ | копия (имена = sha256, иммутабельны) |
| Workspace'ы | `~/.phos/workspace/<uid>/**` | 28 КБ | копия |

Реальный payload ≈ **4–6 МБ**.

### 2.2. Что НЕ бэкапится (и почему)

| Путь | Размер | Причина |
|---|---|---|
| `agent/cache/**` | 3.9 ГБ | regenerable (модели/рантаймы) |
| `~/.omp/profiles/phos/cache/**` | 450 МБ | regenerable |
| `~/.omp/profiles/phos/run/**` | 121 МБ | runtime (pid/sockets) |
| `~/.omp/profiles/phos/logs/**` | 472 КБ | логи, не данные |
| `~/.omp/profiles/phos/puppeteer/**`, `gpu_cache.json` | 8 КБ | runtime |
| `*.db-wal`, `*.db-shm` | — | снапшот через `VACUUM INTO` уже включает WAL-данные |
| `.env` (API-ключ провайдера) | 82 Б | секрет → отдельный secret-backup (§4) |
| `sessions/-tmp-*` | — | тестовый мусор |
| `memories/mnemopi/banks/<bank>` без соответствующего workspace | — | orphan-мусор (в т.ч. `ompcheck-*`) |

Замечание: профиль `phos-it-exp` (интеграционные тесты) в бэкап не входит — бэкапится только профиль из конфига `Omp:Profile`.

## 3. Конфигурация

Новая секция `Backup` (связывается как остальные секции в `Phos.App/Config.fs`):

```json
"Backup": {
  "Enabled": true,
  "Directory": "~/.phos/backups",
  "Interval": "1.00:00:00",
  "AgeRecipient": "~/.config/phos/backup.age.pub",
  "RetainCount": 7
}
```

- `Directory` — куда пишутся архивы; `~` расширяется через существующий `Config.expandHome`; каталог создаётся при старте.
- `Interval` — **`TimeSpan`** (binder понимает `"1.00:00:00"`). Валидация: `> TimeSpan.Zero`, `<= 30 дней`. Это и есть периодичность: бэкап при старте (если `Enabled`) + далее по таймеру `PeriodicTimer(Interval)`.
- `AgeRecipient` — путь к файлу-получателю (публичный ключ, `age-keygen -y` от identity). Хранится **вне** бэкапа.
- `RetainCount` — сколько последних архивов держать (>= 1).

Валидация в `Config.validate`: `Enabled` (bool), непустой `Directory`, корректный `Interval`, `RetainCount >= 1`. `AgeRecipient` существует — проверяется при запуске бэкапа (fail с записью в `backup_log`), а не при старте хоста, чтобы конфиг-ошибка не валила хост.

## 4. Секреты

В основной бэкап **никогда не входят**: bot token, `api_hash`, `.env` профиля (API-ключ), `dev/wtelegram-bot.session`. Правило: никаких секретов в архиве/манифесте/логах.

Отдельный **secret-backup** (за рамками этой фазы, документируется здесь): `age -p`-архив с `.env` + `wtelegram-bot.session` + `appsettings.json` (секреты), identity хранится отдельно от основного. **TODO владельца:** решить, где и как (ручной runbook или автоматика) — в фазе 8/hardening.

При restore профиля `.env` восстанавливается из secret-backup (или заново провижинится `ProfileManager.EnsureProfile` из `SourceProfile`).

## 5. Снапшот: консистентность

- **SQLite-файлы** (`phos.db`, `agent.db`, `models.db`, каждый `mnemopi.db`): `VACUUM INTO '<staging>/<rel>'` на отдельном подключении (не через storage executor), `busy_timeout` как в конфиге, destination не должен существовать (staging создаётся заново на каждый прогон). `VACUUM INTO` даёт консистентный компактный снапшот, включая закоммиченные WAL-данные; копирование `.db` при WAL запрещено.
- **Сессии JSONL**: файл может дописываться живым OMP-процессом. Стратегия: копируем → сравниваем `Length`+`LastWriteTimeUtc` до/после → не изменился: ок; изменился: retry (≤3); всё ещё меняется: **прогон фейлится** (`backup_log.status=failed`) — не отправляем заведомо битый архив. Альтернатива (quiesce всех сессий на время снапшота) отклонена: связывает бэкап с роутером/шедулером, выигрыш не стоит.
- **Блобы**: content-addressed (имя = sha256) — иммутабельны, копия без проверок.
- **Workspace/конфиг**: статичные файлы, обычная копия.
- Disk budget: `VACUUM INTO` требует до ~2× размера DB — при наших размерах несущественно; проверяем свободное место перед снапшотом, при нехватке — fail.

## 6. Формат архива и манифест

Структура снапшота (staging-каталог):

```
<staging>/
  manifest.json
  phos.db
  omp/agent.db
  omp/models.db
  omp/config.yml
  omp/models.yml
  omp/blobs/<sha256>[.ext]
  omp/memories/mnemopi/banks/<bank>/mnemopi.db
  omp/sessions/<ws>/<session>.jsonl
  workspaces/<uid>/...
```

`manifest.json`:

```json
{
  "format": 1,
  "createdAt": "2026-09-17T11:30:00Z",
  "appVersion": "0.x.y",
  "schemaVersion": 11,
  "ompVersion": "18.2.4",
  "files": [
    { "path": "phos.db", "sha256": "...", "size": 2195456 }
  ],
  "payloadBytes": 5242880
}
```

- `schemaVersion` — максимум `VersionInfo.Version` (сейчас 11) — сверяется при restore.
- `appVersion` — из assembly informational version; `ompVersion` — из `omp --version` (или из конфига; информационно, не для проверок).
- SHA-256 по каждому файлу; `payloadBytes` = сумма размеров.

Публикация:
1. `tar.gz` staging (`.NET 10`: `System.Formats.Tar.TarWriter` + `GZipStream`, без внешнего `tar`);
2. `age -R <AgeRecipient> -o <tmp>.age <archive>.tar.gz`;
3. fsync каталога → атомарный rename в `phos-backup-<yyyyMMdd-HHmmss>.age`;
4. внешний SHA-256 файла → `backup_log` (`started_at`, `finished_at`, `path`, `checksum`, `status=ok|failed`, `error`);
5. staging и `.tar.gz` удаляются (успех и провал).

## 7. Retention

После успешной публикации: список `phos-backup-*.age` в `Directory`, отсортированный по имени (имя = timestamp), удалить все, кроме последних `RetainCount`. Записи `backup_log` не удаляются (история). Удаление только успешных/любых файлов архива — по имени-префиксу, без разбора содержимого.

## 8. Restore

### 8.1. Автоматический restore на живых данных — НЕ в v1.

### 8.2. Документированный ручной runbook

1. Остановить phos (systemd stop), убедиться, что OMP-процессы завершены.
2. `age -d -i ~/.config/phos/backup.key phos-backup-<ts>.age > restore.tar.gz` (identity — приватный ключ, отдельно от бэкапа; не путать с recipient).
3. Распаковать; сверить каждый файл по `manifest.json` (sha256 + размер). Несовпадение → STOP, архив повреждён/подменён.
4. Для каждого SQLite: `PRAGMA integrity_check` → `ok`; `VersionInfo` max == `manifest.schemaVersion`; состав таблиц совпадает с ожидаемым.
5. Проверка OMP: `models.yml` парсится и содержит `modelRoles`; каждый `mnemopi.db` открывается (банки на месте); каждый session JSONL — все строки валидный JSON.
6. Замена: `phos.db` → `dev/phos.db`; `omp/**` → `~/.omp/profiles/phos/agent/`; `workspaces/**` → `~/.phos/workspace/`. `.env` — из secret-backup (не из архива).
7. Запустить phos; smoke: `/ping`.

### 8.3. Дрилл (disposable)

Integration-тест: restore в **временный каталог** (не трогая живые `dev/`, `~/.omp`, `~/.phos`) → полная проверка шагов 3–5 → сравнение данных (row counts `users`/`command_inbox`, кол-во банков, кол-во сессий). Это и есть acceptance: «disposable restore возвращает `integrity_check: ok`, корректные схему и данные; OMP-профиль (модель, банки памяти, сессии) восстанавливается».

## 9. Код-структура (для фазы 7)

- `src/Phos.Backup/` — новый проект:
  - `Snapshot.fs` — сбор staging-каталога (VACUUM INTO, копии, retry JSONL);
  - `Manifest.fs` — сборка/проверка манифеста;
  - `Archive.fs` — tar.gz (System.Formats.Tar), age CLI, атомарная публикация, sha256;
  - `Retention.fs` — prune;
  - `BackupService.fs` — `IBackupService` (`RunNow(): Task<Result<unit, string>>`), `BackupHostedService` (старт + `PeriodicTimer`), логирование в `backup_log`;
- `src/Phos.Storage/` — репозиторий `backup_log` (таблица уже есть, миграция 6).
- `src/Phos.App/` — секция `Backup` в конфиге + валидация + wiring hosted service.
- Ключ identity `~/.config/phos/backup.key` (0600) и recipient `~/.config/phos/backup.age.pub` — вне репо, вне бэкапа.

## 10. Acceptance (из плана → как проверяем)

| Пункт | Проверка |
|---|---|
| `integrity_check: ok`, корректная схема/данные | дрилл §8.3: каждый SQLite + VersionInfo=11 + row counts |
| Восстановлен OMP-профиль (модель, банки, сессии) | `models.yml` modelRoles; `mnemopi.db` открывается; JSONL валиден |
| Corrupt/truncated rejected | флип байта в `.age` / обрезка файла / битый tar / повреждённый SQLite → отказ, живые данные не тронуты |

## 11. Тесты (план, TDD-порядок)

1. Config: `TimeSpan`-биндинг `Interval`, валидация (ноль/отрицательный/`>30d` reject), `RetainCount=0` reject.
2. Snapshot SQLite: temp-DB с WAL + конкурентный писатель → `VACUUM INTO` → `integrity_check: ok`, все закоммиченные строки присутствуют.
3. JSONL retry: стабильный файл — ок; постоянно меняющийся — fail после 3 попыток.
4. Manifest: checksum-сверка; подмена одного байта → обнаруживается.
5. age roundtrip (integration, реальный `age`): encrypt → decrypt → сверка; отсутствующий recipient-файл → понятная ошибка в `backup_log`.
6. Corrupt/truncated: каждый класс повреждения → `status=failed`, без файла-архива.
7. Retention: N архивов → prune до `RetainCount`, порядок по времени.
8. Restore drill (integration): полный цикл на temp; сравнение данных; повреждённый архив → отказ.
9. Smoke: бэкап во время тика шедулера/живой сессии — прогон не падает.

## 12. Non-goals / открытые вопросы

- Off-host отгрузка (rclone/rsync/S3) — отложено; интерфейс `IBackupPublisher` закладываем, реализации нет.
- Секрет-backup: ручной runbook, автоматика — решение владельца (фаз 8+).
- Автоматический restore поверх живых данных — нет (только дрилл).
- `backup_now` как host tool / Telegram-команда — не требуется владельцем в v1.
