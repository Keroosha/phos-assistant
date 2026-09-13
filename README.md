# phos-assistant

phos — Telegram-фронтенд персонального ассистента. Принимает сообщения из
Telegram, приводит их в durable-очередь (`command_inbox`) и отвечает через
транзакционный outbox (`message_outbox`). OMP-агент и LLM-обработка — позже
(Phase 5).

## Сборка

```bash
scripts/ci.sh        # полный CI-гейт: сборка + форматтер + линтер + тесты + покрытие
# или только сборка:
dotnet build
```

## Запуск

1. Создайте файл конфигурации `phos.json`:

```json
{
  "databasePath": "data/phos.db",
  "busyTimeoutSeconds": 2,
  "readPoolSize": 4,
  "checkpointEvery": 10,
  "sessionPath": "data/wtelegram-bot.session",
  "users": [{ "id": 123456789, "role": "owner" }],
  "allowedChats": []
}
```

2. Задайте переменные окружения с данными Telegram-приложения:

| Переменная | Откуда |
|---|---|
| `PHOS_TELEGRAM_API_ID` | `api_id` из [my.telegram.org](https://my.telegram.org) |
| `PHOS_TELEGRAM_API_HASH` | `api_hash` из [my.telegram.org](https://my.telegram.org) |
| `PHOS_TELEGRAM_BOT_TOKEN` | токен бота от [@BotFather](https://t.me/BotFather) |

3. Запустите хост:

```bash
dotnet run --project src/Phos.App -- phos.json
```

Путь к конфигу также можно задать через `PHOS_CONFIG`; по умолчанию — `phos.json`.

## Что уже работает

- `/start` (для allowlisted-пользователя) → приветствие, пользователь
  добавляется в таблицу `users`;
- `/ping` → `pong`;
- дедупликация обновлений и retry отправки через outbox;
- любой другой текст/голос попадает в `command_inbox` (обработчик OMP/LLM —
  Phase 5).
