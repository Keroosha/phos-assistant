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

1. Скопируйте пример конфигурации в `appsettings.json` (в корне репозитория, игнорируется git):

```bash
cp appsettings.example.json appsettings.json
```

Заполните `Whitelist.Users[0].Id` вашим Telegram user id (роль `owner`) и добавьте группы/каналы в `AllowedChats`.

2. Задайте секреты через переменные окружения (никогда не храните их в файле):

| Переменная | Откуда |
|---|---|
| `PHOS_TELEGRAM__APIID` | `api_id` из [my.telegram.org](https://my.telegram.org) |
| `PHOS_TELEGRAM__APIHASH` | `api_hash` из [my.telegram.org](https://my.telegram.org) |
| `PHOS_TELEGRAM__BOTTOKEN` | токен бота от [@BotFather](https://t.me/BotFather) |

Обратите внимание на двойное подчёркивание `__` — это разделитель секций `IConfiguration`: `PHOS_TELEGRAM__APIID` соответствует `Telegram:ApiId`.

3. Запустите хост:

```bash
dotnet run --project src/Phos.App
```

Конфигурация собирается из `appsettings.json`, переменных окружения с префиксом `PHOS_` и аргументов командной строки. Переопределить значение можно так:

```bash
dotnet run --project src/Phos.App -- --Storage:DatabasePath /tmp/phos.db
```

## Что уже работает

- `/start` (для allowlisted-пользователя) → приветствие, пользователь
  добавляется в таблицу `users`;
- `/ping` → `pong`;
- дедупликация обновлений и retry отправки через outbox;
- любой другой текст/голос попадает в `command_inbox` (обработчик OMP/LLM —
  Phase 5).
