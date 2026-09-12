# Spike 1 — vanbukin tool call + streaming через Microsoft.Extensions.AI.OpenAI

Дата: 2026-09-12. Хост: Linux x64 (Ryzen 9 9955HX3D), .NET SDK 10.0.103.
Пакеты: `Microsoft.Extensions.AI` 10.10.0, `Microsoft.Extensions.AI.OpenAI` 10.10.0, `OpenAI` 2.13.0.
Эндпоинт: `https://vanbukin.com/v1`, модель `DeepSeek-V4-Flash-Vision-Exp`, ключ из `VANBUKIN_API_KEY`.
Код: `spikes/spike-01-openai/` (throwaway, не входит в Phos.sln).

## Фактический вывод

```
===== Test 1: streaming =====
time_to_first_delta_ms: 4899
delta_count: 16
total_ms: 11080
text: Рекурсия — это метод, при котором функция вызывает саму себя для решения задачи,
      разбивая её на более мелкие подзадачи, пока не будет достигнут базовый случай.

===== Test 2a: tool call =====
first_round_ms: 758
finish_reason: tool_calls
text:
function_calls: [("get_weather", "chatcmpl-tool-8ea310e854a7fcf5", seq [[delegateArg0, Москва]])]

===== Test 2b: streaming tool-call deltas =====
function_call_content_updates: 1
samples: ["get_weather#chatcmpl-tool-861b829201d7dffa"]

===== Test 2c: tool result =====
call_id: chatcmpl-tool-8ea310e854a7fcf5
result: Погода в Москва: +23°C, ясно, ветер 3 м/с.

===== Test 2d: final streaming answer =====
time_to_first_delta_ms: 379
delta_count: 11
total_ms: 492
text: Сейчас в Москве ясная погода, температура воздуха +23°C, ветер 3 м/с.
```

## Выводы

1. **Streaming работает.** TTFT ~4.9 s — это reasoning-модель: первый текстовый дельта приходит после «размышления». Для UX важно: стримить reasoning-контент или использовать `thinking.effort=low` там, где важна скорость. Tool-call roundtrip без reasoning: 758 ms — быстро.
2. **Tool calling работает** (OpenAI-совместимо): `finish_reason: tool_calls`, вызов `get_weather` с аргументом `{"delegateArg0": "Москва"}`, результат подан обратно `FunctionResultContent`, финальный ответ корректен и стримится (TTFT 379 ms).
3. **Tool-call контент стримится** — приходит `FunctionCallContent` update (1 шт. для одного вызова).
4. ⚠️ **Проблема имён параметров в F#**: `AIFunctionFactory.Create(Func<string,string>(...))` из F# сериализует параметр как `delegateArg0` (F#-делегат не несёт имён параметров; у `AIFunctionFactoryOptions` нет `ParameterNames`). Для продакшена нужна явная схема/имена (MethodInfo из C#-хелпера, либо ручной JSON-schema, либо `ConfigureParameterBinding`). Модель корректно передала `delegateArg0`, но читаемость/надёжность контракта страдает.
5. **API-замечания для Phos.Pipeline** (Microsoft.Extensions.AI 10.10.0):
   - `IChatClient` имеет `GetResponseAsync`/`GetStreamingResponseAsync` (не `CompleteAsync`/`CompleteStreamingAsync` — те были в превью).
   - `ChatResponse` не имеет `ToolCalls` — tool calls лежат в `response.Messages` как `FunctionCallContent` (Name/CallId/Arguments).
   - Обратное сообщение с результатом — только `FunctionResultContent` в `Contents`; `ChatMessage(ChatRole.Tool, text)` НЕ сериализуется в tool-сообщение (адаптер читает только `FunctionResultContent`).
   - `Nullability` в F#: `ChatResponseUpdate.Text`/`response.Text` — non-null по метаданным (вычисляются из Contents), `Option.ofObj` на них даёт FS3262; доступ через прямое поле.

## Вердикт

**Не blocker.** Стек подтверждён для фазы 1+: Microsoft.Extensions.AI.OpenAI поверх vanbukin совместим для streaming и tool calling. Требуется доработка контракта имён параметров инструментов (см. п.4) — до production code.
