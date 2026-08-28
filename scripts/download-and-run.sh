#!/bin/sh
set -e
mkdir -p "$(dirname "$MODEL_FILE")"
if [ ! -f "$MODEL_FILE" ]; then
  echo "Downloading embedding model from $MODEL_URL ..."
  curl -L --progress-bar -o "$MODEL_FILE.tmp" "$MODEL_URL"
  mv "$MODEL_FILE.tmp" "$MODEL_FILE"
fi
# shellcheck disable=SC2086
exec llama-server --model "$MODEL_FILE" $LLAMA_ARGS --embedding
