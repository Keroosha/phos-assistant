#!/usr/bin/env python3
"""Reference embeddings for multilingual-e5-small (HuggingFace transformers, torch).

Produces vectors.json: for each test string -> prefix, python token ids
(not padded), python attention-masked mean-pooled L2-normalized vector.
Also runs the exported ONNX model (if onnx_model/ exists) with the SAME
python tokenizer to isolate tokenizer vs runtime differences.
"""
import json
import sys
import torch
import onnxruntime as ort
from transformers import AutoTokenizer, AutoModel

DATA = sys.argv[1] if len(sys.argv) > 1 else "data"

TEXTS = [
    ("query", "как приготовить борщ"),
    ("query", "What is the capital of France?"),
    ("passage", "Борщ — это традиционный русский суп из свёклы и капусты."),
    ("passage", "Paris is the capital of France."),
    ("query", "как работает рекурсия в программировании"),
    ("passage", "Рекурсия — это метод, при котором функция вызывает саму себя."),
    ("passage", "Neural networks are trained with gradient descent."),
    ("query", "чем отличается RRF от взвешенной суммы рангов"),
    ("passage", "Reciprocal Rank Fusion combines ranked lists by summing 1/(k+rank)."),
    ("passage", "ГигаAM — это модель распознавания русской речи."),
]

tok = AutoTokenizer.from_pretrained(DATA)
model = AutoModel.from_pretrained(DATA)
model.eval()

results = {}
for prefix, text in TEXTS:
    s = f"{prefix}: {text}"
    enc = tok(s, padding="max_length", max_length=512, truncation=True,
              return_tensors="pt")
    with torch.no_grad():
        out = model(**enc)
    last = out.last_hidden_state
    mask = enc["attention_mask"].unsqueeze(-1).float()
    pooled = (last * mask).sum(dim=1) / mask.sum(dim=1)
    pooled = torch.nn.functional.normalize(pooled, p=2, dim=1)
    results[s] = {
        "prefix": prefix,
        "text": text,
        "ids_unpadded": tok(s, truncation=True, max_length=512)["input_ids"],
        "vec": [float(x) for x in pooled[0].tolist()],
    }

# Optional: same inputs through exported ONNX, to isolate tokenizer vs weights.
try:
    sess = ort.InferenceSession("onnx_model/model.onnx", providers=["CPUExecutionProvider"])
    for s, r in results.items():
        enc = tok(r["prefix"] + ": " + r["text"], padding="max_length",
                  max_length=512, truncation=True, return_tensors="np")
        enc["token_type_ids"] = enc.get("token_type_ids", __import__("numpy").zeros_like(enc["input_ids"]))
        feed = {k: v.astype("int64") for k, v in enc.items()}
        onnx_out = sess.run(None, feed)[0]
        mask = enc["attention_mask"].astype("float32")[..., None]
        pooled = (onnx_out * mask).sum(axis=1) / mask.sum(axis=1)
        pooled = pooled / (pooled * pooled).sum(axis=1, keepdims=True) ** 0.5
        r["onnx_python_vec"] = [float(x) for x in pooled[0].tolist()]
    print("ONNX (python tokenizer): computed")
except Exception as e:
    print("ONNX (python tokenizer): skipped —", e)

with open("vectors.json", "w") as f:
    json.dump(results, f, ensure_ascii=False)
print("wrote vectors.json with", len(results), "entries")
