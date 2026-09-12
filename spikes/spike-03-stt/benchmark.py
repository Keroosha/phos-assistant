#!/usr/bin/env python3
"""GigaAM v3 CTC int8 CPU benchmark via sherpa-onnx.

Usage: benchmark.py <num_threads>
Prints a JSON summary: per-file RTF, p50/p95 RTF, peak RSS (MiB), sample text.
Each invocation is a separate process so ru_maxrss is per-config.
"""
import json
import resource
import sys
import time

import numpy as np
import sherpa_onnx
import soundfile as sf

THREADS = int(sys.argv[1])
FILES = [
    "data/fix_example.wav",
    "data/fix_5s.wav",
    "data/fix_10s.wav",
    "data/fix_15s.wav",
    "data/fix_20s.wav",
    "data/fix_25s.wav",
    "data/fix_30s.wav",
]

rec = sherpa_onnx.OfflineRecognizer.from_nemo_ctc(
    model="data/model.int8.onnx",
    tokens="data/tokens.txt",
    num_threads=THREADS,
    decoding_method="greedy_search",
    provider="cpu",
    debug=False,
)

rtf = []
last_text = ""
for f in FILES:
    samples, sr = sf.read(f, dtype="float32")
    assert sr == 16000, f
    duration = len(samples) / sr
    stream = rec.create_stream()
    stream.accept_waveform(16000, samples)
    t0 = time.perf_counter()
    rec.decode_stream(stream)
    elapsed = time.perf_counter() - t0
    rtf.append(elapsed / duration)
    last_text = stream.result.text

print(json.dumps({
    "threads": THREADS,
    "rtf": [round(x, 4) for x in rtf],
    "p50": round(float(np.percentile(rtf, 50)), 4),
    "p95": round(float(np.percentile(rtf, 95)), 4),
    "peak_rss_mb": round(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024, 1),
    "text_sample": last_text[:120],
}, ensure_ascii=False))
