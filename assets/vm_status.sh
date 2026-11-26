#!/bin/bash
# Combined VM health + optional per-job translation progress check (PROTO 2).
# Usage: vm_status.sh [job_dir]
# MANGA_CONTENT_ROOT overrides the /content root for offline tests only.
CONTENT_ROOT="${MANGA_CONTENT_ROOT:-/content}"
JOB_DIR="${1:-}"

echo "PROTO 2"

if curl -s -m 3 http://127.0.0.1:6006/health 2>/dev/null | grep -q ok; then
  echo "LLAMA_OK"
else
  echo "LLAMA_DOWN"
fi

MIT_CODE=$(curl -s -m 3 -o /dev/null -w "%{http_code}" http://127.0.0.1:8001/docs 2>/dev/null || echo "000")
if [ "$MIT_CODE" = "200" ]; then
  echo "MIT_OK"
else
  echo "MIT_DOWN"
fi

# Engine truth lives on the VM: the deploy marker wins; a v1.3.x install
# (mit dir + font, no marker) still reports as a usable mit session.
if [ -f "$CONTENT_ROOT/engine_ready" ]; then
  ENGINE_NAME=$(sed -n 's/^ENGINE=\(.*\)$/\1/p' "$CONTENT_ROOT/engine_ready" | head -n1)
  case "$ENGINE_NAME" in mit|mtui)
    echo "ENGINE $ENGINE_NAME ready" ;;
  *)
    echo "ENGINE none missing" ;;
  esac
  for key in GPU_PROFILE GPU_VRAM_MB INPAINT_MODE; do
    value=$(sed -n "s/^${key}=//p" "$CONTENT_ROOT/engine_ready" | head -n1)
    [ -z "$value" ] || echo "$key $value"
  done
  port=$(sed -n 's/^FALLBACK_PORT=//p' "$CONTENT_ROOT/engine_ready" | head -n1)
  if [ -n "$port" ]; then
    if [[ "$port" =~ ^[0-9]+$ ]] && curl -fsS -m 3 "http://127.0.0.1:${port}/health" 2>/dev/null | grep -q ok; then
      echo "FALLBACK_LLAMA_OK"
    else
      echo "FALLBACK_LLAMA_DOWN"
    fi
  fi
elif [ ! -f "$CONTENT_ROOT/.gui_engine_proto" ] \
  && [ -d "$CONTENT_ROOT/manga-image-translator" ] \
  && [ -s "$CONTENT_ROOT/manga-image-translator/fonts/NotoSansCJKsc-Regular.otf" ]; then
  # Legacy v1.3.x deploy (no marker, no engine_ready). A new-protocol deploy writes
  # .gui_engine_proto at lock acquisition, so mid/failed new deploys never pass as mit.
  echo "ENGINE mit ready"
else
  echo "ENGINE none missing"
fi

GPU_INFO=$(nvidia-smi --query-gpu=name,memory.used,memory.total --format=csv,noheader 2>/dev/null | head -n 1 || echo "N/A")
echo "GPU $GPU_INFO"

if [ -n "$JOB_DIR" ]; then
  OUT_DIR=$(cat "$JOB_DIR/out_path" 2>/dev/null || true)
  if [ -z "$OUT_DIR" ]; then
    OUT_DIR="$JOB_DIR/out"
  fi
  PID_FILE="$JOB_DIR/pid"
  EXIT_FILE="$JOB_DIR/exit_code"
  LOG_FILE="$JOB_DIR/translate.log"

  DONE_COUNT=0
  if [ -d "$OUT_DIR" ]; then
    DONE_COUNT=$(find "$OUT_DIR" -maxdepth 1 -type f ! -name '.*' \
      \( -iname '*.jpg' -o -iname '*.jpeg' -o -iname '*.png' -o -iname '*.webp' -o -iname '*.bmp' -o -iname '*.tif' -o -iname '*.tiff' \) \
      2>/dev/null | wc -l | tr -d ' ')
  fi

  JOB_ALIVE=0
  if [ -s "$PID_FILE" ]; then
    PID=$(cat "$PID_FILE" 2>/dev/null | tr -d '[:space:]')
    if [[ "$PID" =~ ^[0-9]+$ ]] && kill -0 "$PID" 2>/dev/null; then
      JOB_ALIVE=1
    fi
  fi

  JOB_EXIT="RUNNING"
  if [ -s "$EXIT_FILE" ]; then
    JOB_EXIT=$(cat "$EXIT_FILE" 2>/dev/null | tr -d '[:space:]')
  fi

  echo "DONE_COUNT $DONE_COUNT"
  echo "JOB_ALIVE $JOB_ALIVE"
  echo "TRANSLATE_ALIVE $JOB_ALIVE"
  echo "JOB_EXIT_CODE $JOB_EXIT"

  if [ -f "$LOG_FILE" ]; then
    # mit logs "Saving \"...\""; mtui logs "[BATCH] Saved successfully: <file>".
    LAST_SAVING=$(grep -aE "Saving|Saved successfully" "$LOG_FILE" 2>/dev/null | tail -n 1 || true)
    if [ -n "$LAST_SAVING" ]; then
      echo "LAST_SAVING $LAST_SAVING"
    fi
  fi
fi
