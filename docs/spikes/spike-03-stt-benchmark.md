# Spike 3 — GigaAM v3 CTC int8 через sherpa-onnx: CPU benchmark

Дата: 2026-09-12. Хост: Linux x64, AMD Ryzen 9 9955HX3D (16C/32T), RAM 60 GiB, CPU-only (без GPU-провайдера).
Модель: `csukuangfj/sherpa-onnx-nemo-ctc-giga-am-v3-russian-2025-12-16`, `model.int8.onnx` (224 721 476 байт), `tokens.txt`.
Рантайм: sherpa-onnx **1.13.8** (Python cp312, manylinux x86_64), onnxruntime (внутри sherpa), decoding `greedy_search`, provider cpu.
Код: `spikes/spike-03-stt/benchmark.py`, фикстуры: RU-речь, 16 kHz mono PCM16, длительности 5/10/11.3/15/20/25/30 с (7 файлов, нарезаны из `long_example.wav` + `example.wav`).

## Фактический вывод

| threads | p50 RTF | p95 RTF | min | max | peak RSS (MiB) |
|---|---|---|---|---|---|
| 1 | 0.0718 | 0.0758 | 0.0706 | 0.0763 | 698.1 |
| 2 | 0.0395 | 0.0415 | 0.0391 | 0.0418 | 693.1 |
| 4 | 0.0240 | 0.0246 | 0.0235 | 0.0246 | 693.4 |
| 8 | 0.0174 | 0.0188 | 0.0162 | 0.0191 | 693.0 |
| 16 | 0.0156 | 0.0185 | 0.0153 | 0.0188 | 693.4 |

Пример распознавания (все конфигурации идентичны, детерминировано):
«но в кельях тихо и темно уже и сам игумен строгий свои молитвы прекратил и кости ветхие склонил перекрестясь на одр убог» — текст корректный.

## Выводы

1. **GigaAM v3 CTC int8 работает в sherpa-onnx 1.13.8** через `OfflineRecognizer.from_nemo_ctc(model, tokens, num_threads, decoding_method="greedy_search", provider="cpu")`. (API в 1.13.8: `from_nemo_ctc`, а не `OfflineCtcModelConfig`/`from_config` — они для других семейств.)
2. **RTF на этом CPU: 0.072 (1 thread) → 0.016 (16 threads)**. Даже 1 поток даёт 14× realtime; 4 потока — ~42×. Опубликованный пример v2 (0.329 при 2 threads) несравним: другая модель/CPU; наш результат — факт для целевого хоста.
3. **Отдача от потоков падает после 8**: 8→16 даёт p50 0.0174→0.0156, p95 без улучшения (0.0188→0.0185). Рекомендация: **num_threads=4** как дефолт для STT-семофора (RTF 0.024, низкая дисперсия), 8 — если STT-очередь станет узким местом. Дополнительные потоки не дают выигрыша, а забирают CPU у LLM/MCP.
4. **Peak RSS ~693–698 MiB** независимо от числа потоков (int8-модель 225 МБ + onnxruntime arena + fbank). Это верхняя граница для планирования памяти: один STT-процесс ≈ 0.7 GiB. В production — отдельный процесс/сервис (по плану), а не в-процессная библиотека в Telegram-хост.
5. **Детерминированность**: текст одинаков на всех конфигурациях (greedy_search).
6. Модель не входит в основную страницу release models (репозиторий существует на HF с README и скриптами) — при пинге нужен checksum: sha256 `model.int8.onnx` зафиксировать при интеграции (фаза 5); здесь файл скачан напрямую из официального репо `csukuangfj`.

## Замечание по лицензии

Репозиторий `csukuangfj/sherpa-onnx-nemo-ctc-giga-am-v3-russian-2025-12-16` содержит LICENSE из `salute-developers/GigaAM` (MIT для v3, но проверить конкретный checkpoint при интеграции; в модели v3 — MIT по информации в research-plan; нужен повторный аудит файла LICENSE при пинге).

## Вердикт

**Не blocker.** GigaAM v3 CTC int8 подтверждён как Primary для фазы 5; benchmark записан для целевого CPU; дефолт num_threads=4.
