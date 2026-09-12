# Spike 2 — multilingual-e5-small: .NET vs Python/HuggingFace parity

Дата: 2026-09-12. Хост: Linux x64 (Ryzen 9 9955HX3D), .NET SDK 10.0.103.
Модель: `intfloat/multilingual-e5-small` (XLM-R, 384-dim, vocab 250037, max_len 512).
Python (reference): torch 2.14.0+cu130 (CPU), transformers 5.17.0, onnxruntime 1.30.0, optimum 2.3.0 (`optimum[onnx]`).
.NET: `Microsoft.ML.OnnxRuntime` 1.30.0 + `Microsoft.ML.Tokenizers` 2.0.0 (`SentencePieceTokenizer`).
ONNX: экспорт `optimum-cli export onnx --task feature-extraction` (входы `input_ids`/`attention_mask`/`token_type_ids`, выход `last_hidden_state`).
Код: `spikes/spike-02-embeddings/` (`reference.py` + F#), данные — `data/`, модель — `onnx_model/` (gitignored).

## Фактический вывод (10 строк RU/EN, query/passage)

```
total: 10, token parity: 10/10
min cosine (dotnet vs python torch): 1.000000
min cosine (dotnet vs python-onnx): 1.000000
```

Каждая строка: `cos=1.000000 (onnx=1.000000) tokens_match=true`.

## Критическая находка: id-remap XLM-R

`Microsoft.ML.Tokenizers.SentencePieceTokenizer` выдаёт **сырые SentencePiece id**:
`<unk>`=0, `<s>`=1, `</s>`=2, `<pad>` — отсутствует (pad_id=-1), куски с 3.
HuggingFace `XLMRobertaTokenizer` использует другой vocab: `<s>`=0, `<pad>`=1, `</s>`=2, `<unk>`=3, куски с 4.

Маппинг (обязателен в Phos.Memory):

```fsharp
let remapToHf (rawId: int) =
    match rawId with
    | 1 -> 0     // <s>
    | 0 -> 3     // <unk>
    | 2 -> 2     // </s>
    | id -> id + 1  // pieces (raw >= 3)
// pad id = 1 (<pad>), маска 0 для padding
```

Без remap cosine ~0.72–0.80 (0/10 токенов совпало), с remap — 1.000000 (10/10).

## Проверенные детали pipeline

- `SentencePieceTokenizer.Create(modelStream, addBeginningOfSentence=true, addEndOfSentence=true, specialTokens)` совпадает с HF `tokenizer(text)` (BOS/EOS добавляются).
- Нормализация (precompiled charsmap) и пре-токенизация (Metaspace `▁`, add_prefix_space) в .NET совпадают с HF — сегментация идентична (доказано parity токенов).
- ONNX экспорт корректный: Python-torch == Python-ONNX == .NET-ONNX (cos 1.0).
- Mean pooling по attention_mask + L2 normalize (как в плане §2.3) даёт бит-в-бит-близкий вектор (cos=1.000000; фактически fp32, различие < 1e-6, округляется до 1.0).
- Входы ONNX: `input_ids`, `attention_mask`, `token_type_ids` (нулевые) — экспорт требует все три; без `token_type_ids` optimum-модель падает.

## Выводы для Phos.Memory

1. Стек подтверждён: ONNX Runtime .NET + ML.Tokenizers + optimum-экспорт дают полный parity с Python/HF на RU/EN.
2. **В БД хранить ids в HF-формате** (как считает Python/эталон), т.е. после remap; переиндексация не требуется, т.к. векторы совпадают.
3. Параметры модели: `embedding_model=intfloat/multilingual-e5-small`, `embedding_version` = sha256 экспортированного ONNX (решение о хранении — фаза 6).
4. На производительность (latency/RSS) на этом CPU — отдельный вопрос (не в scope spike 2; exact-scan benchmark — фаза 1+).

## Вердикт

**Не blocker.** Parity достигнут; требование «точный tokenizer + prefixes + max 512 + attention-mask mean pooling + L2» подтверждено.
