#!/usr/bin/env bash
# Provisions local STT models and the WER test fixture for Phos Phase 4.
#
# Downloads (if missing) and verifies the sha256 of:
#   - GigaAM v3 CTC int8 (model.int8.onnx + tokens.txt)  [Hugging Face]
#   - Silero VAD (silero_vad.onnx)                        [GitHub releases]
#   - Whisper small multilingual int8 (encoder/decoder/tokens) [Hugging Face]
#   - long_example.wav, from which the WER fixture (ru_example.wav) is cut
#
# Idempotent: existing files whose sha256 matches are skipped. A mismatch
# exits 1. No sudo; writes only under the target dir (git-ignored).
#
# Usage: scripts/provision-stt.sh [--dir <target-dir>]
set -euo pipefail

DIR="spikes/spike-03-stt/data"
while [[ $# -gt 0 ]]; do
    case "$1" in
        --dir)
            DIR="$2"
            shift 2
            ;;
        *)
            echo "unknown option: $1" >&2
            exit 1
            ;;
    esac
done

mkdir -p "$DIR"

# sha256 of every downloaded artifact (pinned).
MODEL_SHA="f86ebfa0429ced91be6054fc344827e9c6c2572f3c318416cd974b06f66437ec"
TOKENS_SHA="17cc514451bcceac9c280068c71502f8448f99e9fb1456b8d0761651fd0392f2"
VAD_SHA="9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6"
W_ENCODER_SHA="4cbe7b22fa9026b843b60a68640c747de05bafb1a11b57edc0e66c232d9f33a9"
W_DECODER_SHA="acad50b5c782696e91b55914cc5ab4f756f1532f76e22aa6fc615f39fb69a8ee"
W_TOKENS_SHA="b34b360dbb493e781e479794586d661700670d65564001f23024971d1f2fa126"
LONG_EXAMPLE_SHA="1868ece0195dfa9fc2394be24865d1133c8452a8292a397db45ba8c3ed9e01e3"
RU_EXAMPLE_SHA="34a594de03723005059d855e6d602a61f48e98915b6f6d88118298dae5c917a1"

MODEL_URL="https://huggingface.co/csukuangfj/sherpa-onnx-nemo-ctc-giga-am-v3-russian-2025-12-16/resolve/main/model.int8.onnx"
TOKENS_URL="https://huggingface.co/csukuangfj/sherpa-onnx-nemo-ctc-giga-am-v3-russian-2025-12-16/resolve/main/tokens.txt"
VAD_URL="https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx"
W_ENCODER_URL="https://huggingface.co/csukuangfj/sherpa-onnx-whisper-small/resolve/main/small-encoder.int8.onnx"
W_DECODER_URL="https://huggingface.co/csukuangfj/sherpa-onnx-whisper-small/resolve/main/small-decoder.int8.onnx"
W_TOKENS_URL="https://huggingface.co/csukuangfj/sherpa-onnx-whisper-small/resolve/main/small-tokens.txt"
LONG_EXAMPLE_URL="https://huggingface.co/csukuangfj/tmp-files/resolve/main/GigaAM/long_example.wav"

# Fetches a file if missing or its sha256 does not match the pinned value.
# Exits 1 on a checksum mismatch of an existing file or after a download.
fetch() {
    local url="$1" path="$2" sha="$3"
    if [[ -f "$path" ]]; then
        local actual
        actual="$(sha256sum "$path" | awk '{print $1}')"
        if [[ "$actual" == "$sha" ]]; then
            echo "ok   $path (cached)"
            return 0
        fi
        echo "mismatch: $path expected $sha got $actual" >&2
        exit 1
    fi
    echo "get  $path"
    curl -fSL --retry 3 -o "$path.tmp" "$url"
    local actual
    actual="$(sha256sum "$path.tmp" | awk '{print $1}')"
    if [[ "$actual" != "$sha" ]]; then
        rm -f "$path.tmp"
        echo "mismatch: $path expected $sha got $actual" >&2
        exit 1
    fi
    mv "$path.tmp" "$path"
}

fetch "$MODEL_URL" "$DIR/model.int8.onnx" "$MODEL_SHA"
fetch "$TOKENS_URL" "$DIR/tokens.txt" "$TOKENS_SHA"
fetch "$VAD_URL" "$DIR/silero_vad.onnx" "$VAD_SHA"

WHISPER_DIR="$DIR/whisper"
mkdir -p "$WHISPER_DIR"
fetch "$W_ENCODER_URL" "$WHISPER_DIR/small-encoder.int8.onnx" "$W_ENCODER_SHA"
fetch "$W_DECODER_URL" "$WHISPER_DIR/small-decoder.int8.onnx" "$W_DECODER_SHA"
fetch "$W_TOKENS_URL" "$WHISPER_DIR/small-tokens.txt" "$W_TOKENS_SHA"

# WER fixture: cut the canonical «но в кельях…» passage from long_example.wav.
# The passage starts at 2.0s and the 10.5s cut yields the canonical reference
# text (verified: sha256 34a594de…). Pure byte-range copy via python3 — no
# ffmpeg needed (its WAV muxer embeds a version-dependent LIST/INFO chunk).
if [[ -f "$DIR/ru_example.wav" ]]; then
    local_ru="$(sha256sum "$DIR/ru_example.wav" | awk '{print $1}')"
    if [[ "$local_ru" != "$RU_EXAMPLE_SHA" ]]; then
        echo "mismatch: $DIR/ru_example.wav expected $RU_EXAMPLE_SHA got $local_ru" >&2
        exit 1
    fi
    echo "ok   $DIR/ru_example.wav (cached)"
else
    if [[ ! -f "$DIR/long_example.wav" ]]; then
        fetch "$LONG_EXAMPLE_URL" "$DIR/long_example.wav" "$LONG_EXAMPLE_SHA"
    fi
    echo "cut  $DIR/ru_example.wav"
    # Byte-exact cut: the input is already 16 kHz mono s16le, so the 2.0s /
    # 10.5s passage is a pure byte-range copy — deterministic regardless of
    # audio tooling (ffmpeg's WAV muxer embeds a version-dependent LIST/INFO
    # chunk, which made the fixture sha unstable across runners). Canonical
    # RIFF header, no encoder metadata.
    python3 - "$DIR/long_example.wav" "$DIR/ru_example.wav" <<'PY'
import struct, sys

src, dst = sys.argv[1], sys.argv[2]
data = open(src, "rb").read()
assert data[:4] == b"RIFF" and data[8:12] == b"WAVE"
off = 12
while off + 8 <= len(data):
    cid = data[off:off + 4]
    csz = struct.unpack("<I", data[off + 4:off + 8])[0]
    if cid == b"data":
        data_off = off + 8
        break
    off += 8 + csz + (csz & 1)
start = 2 * 16000 * 2                # 2.0 s at 16 kHz mono s16le
length = 10 * 16000 * 2 + 16000      # 10.5 s
payload = data[data_off + start:data_off + start + length]
assert len(payload) == length, len(payload)
fmt = data[12:data_off - 8]          # fmt chunk as-is
out = (b"RIFF" + struct.pack("<I", 4 + len(fmt) + 8 + len(payload)) + b"WAVE"
       + fmt + b"data" + struct.pack("<I", len(payload)) + payload)
open(dst, "wb").write(out)
PY
    local_ru="$(sha256sum "$DIR/ru_example.wav" | awk '{print $1}')"
    if [[ "$local_ru" != "$RU_EXAMPLE_SHA" ]]; then
        echo "mismatch: $DIR/ru_example.wav expected $RU_EXAMPLE_SHA got $local_ru" >&2
        exit 1
    fi
fi

echo "STT provisioning complete."
