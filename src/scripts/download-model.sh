#!/usr/bin/env bash
set -euo pipefail

DEST="${EMBEDDING_MODEL_DIR:-models/all-MiniLM-L6-v2}"
# Default to ModelScope (国内可达); HF_ENDPOINT 可覆盖 (e.g. https://hf-mirror.com 或 https://huggingface.co)
ENDPOINT="${HF_ENDPOINT:-https://www.modelscope.cn}"
REPO="${HF_REPO:-sentence-transformers/all-MiniLM-L6-v2}"
# ModelScope 用 master 分支，HF 用 main
BRANCH="${HF_BRANCH:-$( [[ "$ENDPOINT" == *modelscope.cn* ]] && echo master || echo main )}"

mkdir -p "$DEST"

declare -A FILES=(
  [model.onnx]="onnx/model.onnx"
  [tokenizer.json]="tokenizer.json"
  [vocab.txt]="vocab.txt"
  [config.json]="config.json"
)

for name in "${!FILES[@]}"; do
  path="${FILES[$name]}"
  url="$ENDPOINT/$REPO/resolve/$BRANCH/$path"
  if [[ -f "$DEST/$name" ]]; then
    echo "[skip] $DEST/$name already exists"
    continue
  fi
  echo "[fetch] $url -> $DEST/$name"
  curl -fSL "$url" -o "$DEST/$name"
done

echo "Done. Model files in $DEST"