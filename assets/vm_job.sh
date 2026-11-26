#!/bin/bash
# Task-isolated manga translation launcher for MangaGui (PROTO 2).
# Usage: vm_job.sh start <job_dir> [expected_image_count] [config_file]
#
# The engine is read from /content/engine_ready (written by vm_setup.sh), never
# from GUI-side assumptions. Writes $JOB_DIR/out_path with the real output image
# directory before launching so status/pack stay engine-agnostic.
# MANGA_CONTENT_ROOT overrides the /content root for offline tests only.
set -euo pipefail
export MPLBACKEND=Agg
unset FALLBACK_SAKURA_API_BASE

CONTENT_ROOT="${MANGA_CONTENT_ROOT:-/content}"
CMD="${1:-}"
JOB_DIR="${2:-}"
EXPECTED_COUNT="${3:-0}"
CONFIG_FILE="${4:-$CONTENT_ROOT/manga.json}"

if [ "$CMD" != "start" ]; then
  echo "JOB_ERROR unknown_cmd $CMD" >&2
  exit 1
fi

if [[ -z "$JOB_DIR" || "$JOB_DIR" != "$CONTENT_ROOT"/jobs/job_* || "$JOB_DIR" == *".."* ]]; then
  echo "JOB_ERROR unsafe_job_dir $JOB_DIR" >&2
  exit 1
fi

ENGINE=
GPU_PROFILE=l4_24g
PRIMARY_MODEL=
FALLBACK_PORT=
if [ -f "$CONTENT_ROOT/engine_ready" ]; then
  while IFS='=' read -r key value || [ -n "$key" ]; do
    case "$key" in
      ENGINE) ENGINE=$value ;;
      GPU_PROFILE) GPU_PROFILE=${value:-l4_24g} ;;
      PRIMARY_MODEL) PRIMARY_MODEL=$value ;;
      FALLBACK_PORT) FALLBACK_PORT=$value ;;
    esac
  done < "$CONTENT_ROOT/engine_ready"
elif [ ! -f "$CONTENT_ROOT/.gui_engine_proto" ] \
  && [ -d "$CONTENT_ROOT/manga-image-translator" ] \
  && [ -s "$CONTENT_ROOT/manga-image-translator/fonts/NotoSansCJKsc-Regular.otf" ]; then
  # Legacy v1.3.x deploy (same rule as vm_status.sh): marker absent + mit dir + font.
  ENGINE=mit
else
  echo "JOB_ERROR engine_not_ready" >&2
  exit 1
fi
case "$ENGINE" in mit|mtui) ;; *)
  echo "JOB_ERROR engine_not_ready" >&2
  exit 1 ;;
esac

JOB_BASE=$(basename "$JOB_DIR")
STAGED_ZIP="$CONTENT_ROOT/${JOB_BASE}.zip"
ZIP_FILE="$JOB_DIR/input.zip"
SRC_DIR="$JOB_DIR/src"
OUT_DIR="$JOB_DIR/out"
ENGINE_OUT_DIR="$JOB_DIR/engine_out"
LOG_FILE="$JOB_DIR/translate.log"
PID_FILE="$JOB_DIR/pid"
EXIT_FILE="$JOB_DIR/exit_code"
OUT_PATH_FILE="$JOB_DIR/out_path"
MTUI_DIR="$CONTENT_ROOT/mtui"
MTUI_CONFIG="$CONTENT_ROOT/mtui_config.json"
MIT_DIR="$CONTENT_ROOT/manga-image-translator"

mkdir -p "$JOB_DIR"
if [ ! -f "$ZIP_FILE" ] && [ -f "$STAGED_ZIP" ]; then
  mv -f "$STAGED_ZIP" "$ZIP_FILE"
fi

if [ ! -f "$ZIP_FILE" ]; then
  echo "JOB_ERROR missing_input_zip $ZIP_FILE" >&2
  exit 1
fi

if [ "$ENGINE" = "mit" ] && [ ! -f "$CONFIG_FILE" ]; then
  echo "JOB_ERROR missing_config $CONFIG_FILE" >&2
  exit 1
fi
if [ "$ENGINE" = "mtui" ] && [ ! -f "$MTUI_CONFIG" ]; then
  echo "JOB_ERROR missing_config $MTUI_CONFIG" >&2
  exit 1
fi

rm -rf "$SRC_DIR" "$OUT_DIR" "$ENGINE_OUT_DIR" "$PID_FILE" "$EXIT_FILE" "${EXIT_FILE}.tmp" "$OUT_PATH_FILE" "${OUT_PATH_FILE}.tmp"
mkdir -p "$SRC_DIR" "$OUT_DIR"

if ! unzip -q -o "$ZIP_FILE" -d "$SRC_DIR"; then
  echo "JOB_ERROR unzip_failed $ZIP_FILE" >&2
  exit 1
fi

ACTUAL_COUNT=$(find "$SRC_DIR" -maxdepth 1 -type f ! -name '.*' \
  \( -iname '*.jpg' -o -iname '*.jpeg' -o -iname '*.png' -o -iname '*.webp' -o -iname '*.bmp' -o -iname '*.tif' -o -iname '*.tiff' \) \
  2>/dev/null | wc -l | tr -d ' ')

if [ "$ACTUAL_COUNT" -le 0 ]; then
  echo "JOB_ERROR no_extracted_images $SRC_DIR" >&2
  exit 1
fi

if [ "$EXPECTED_COUNT" -gt 0 ] && [ "$ACTUAL_COUNT" -ne "$EXPECTED_COUNT" ]; then
  echo "JOB_ERROR extract_count_mismatch expected=$EXPECTED_COUNT actual=$ACTUAL_COUNT" >&2
  exit 1
fi

JOB_MTUI_CONFIG=$MTUI_CONFIG
if [ "$ENGINE" = mtui ] && [ "$GPU_PROFILE" != l4_24g ] && [ -f "$CONTENT_ROOT/mtui_active_config.json" ]; then
  prompt="$JOB_DIR/prompt.yaml"
  if [ -f "$CONTENT_ROOT/prompt_hq.yaml" ]; then
    cp "$CONTENT_ROOT/prompt_hq.yaml" "$prompt"
  else
    # Older asset uploads may lack the custom template. Keep glossary writes job-local.
    cp "$MTUI_DIR/dict/prompt_example.yaml" "$prompt"
  fi
  JOB_MTUI_CONFIG="$JOB_DIR/mtui_job_config.json"
  "$MTUI_DIR/.venv/bin/python" -I - "$CONTENT_ROOT/mtui_active_config.json" "$JOB_MTUI_CONFIG" "$prompt" "$SRC_DIR" <<'PY'
import json, statistics, sys
from pathlib import Path

base, dest, prompt, src = map(Path, sys.argv[1:])
config = json.loads(base.read_text(encoding='utf-8'))
config['translator']['high_quality_prompt_path'] = str(prompt)
short_sides = []
try:
    from PIL import Image
    extensions = {'.jpg', '.jpeg', '.png', '.webp', '.bmp', '.tif', '.tiff'}
    images = sorted(p for p in src.iterdir() if not p.name.startswith('.') and p.suffix.lower() in extensions)
    for image in images[:5]:
        try:
            with Image.open(image) as page:
                short_sides.append(min(page.size))
        except (OSError, ValueError, Image.DecompressionBombError):
            pass
except ImportError:
    pass
low_res = bool(short_sides and statistics.median(short_sides) < 900)
config['upscale']['upscaler'] = 'mangajanai'
config['upscale']['upscale_ratio'] = 'x2' if low_res else None
dest.write_text(json.dumps(config, ensure_ascii=False, indent=2), encoding='utf-8')
PY
  export OPENAI_API_KEY=local OPENAI_API_BASE=http://127.0.0.1:6006/v1 OPENAI_MODEL="$PRIMARY_MODEL"
  if [[ "$FALLBACK_PORT" =~ ^[0-9]+$ ]]; then
    export FALLBACK_SAKURA_API_BASE="http://127.0.0.1:${FALLBACK_PORT}/v1"
  fi
fi

# mtui writes finished pages to <output>/<input dir name>/, i.e. engine_out/src.
if [ "$ENGINE" = "mtui" ]; then
  REAL_OUT_DIR="$ENGINE_OUT_DIR/$(basename "$SRC_DIR")"
else
  REAL_OUT_DIR="$OUT_DIR"
fi
printf '%s\n' "$REAL_OUT_DIR" > "${OUT_PATH_FILE}.tmp" && mv -f "${OUT_PATH_FILE}.tmp" "$OUT_PATH_FILE"

if [ "$ENGINE" = "mit" ]; then
  source "$CONTENT_ROOT/env.sh"
  cd "$MIT_DIR"
fi
export SAKURA_API_BASE=http://127.0.0.1:6006/v1
export SAKURA_VERSION=0.10
# An existing-but-empty dict makes mit's SakuraDict recurse to RecursionError; a
# nonexistent path is handled cleanly by both engines, so point at a disabled
# path unless the file holds at least one `src->dst` or TAB-separated entry.
if grep -qE -- '->|	' "$CONTENT_ROOT/sakura_dict.txt" 2>/dev/null; then
  export SAKURA_DICT_PATH="$CONTENT_ROOT/sakura_dict.txt"
else
  export SAKURA_DICT_PATH="$CONTENT_ROOT/sakura_dict.disabled"
fi

if [ "$ENGINE" = "mtui" ]; then
  # Debug artifacts are verbose-only; clear anyway so a stale dir never survives a job.
  rm -rf "$MTUI_DIR/result"
fi

# Launch wrapped worker that records exact child PID and atomically writes exit_code on exit.
# setsid + /dev/null stdin detach it from the ssh session so the job survives disconnects.
setsid nohup bash -c '
  set +e
  ENGINE="$1"
  JOB_DIR="$2"
  SRC_DIR="$3"
  OUT_DIR="$4"
  ENGINE_OUT_DIR="$5"
  REAL_OUT_DIR="$6"
  CONFIG_FILE="$7"
  MTUI_DIR="$8"
  MTUI_CONFIG="$9"
  MIT_DIR="${10}"
  LOG_FILE="$JOB_DIR/translate.log"
  PID_FILE="$JOB_DIR/pid"
  EXIT_FILE="$JOB_DIR/exit_code"

  cd_ok=1
  if [ "$ENGINE" = "mtui" ]; then
    cd "$MTUI_DIR" || cd_ok=0
  else
    cd "$MIT_DIR" || cd_ok=0
  fi
  if [ "$cd_ok" -eq 0 ]; then
    printf "1\n" > "${EXIT_FILE}.tmp" && mv -f "${EXIT_FILE}.tmp" "$EXIT_FILE"
    exit 1
  fi

  if [ "$ENGINE" = "mtui" ]; then
    QT_QPA_PLATFORM=offscreen MANGA_JA_ONLY=1 \
      "$MTUI_DIR/.venv/bin/python" -m manga_translator local \
      -i "$SRC_DIR" -o "$ENGINE_OUT_DIR" --config "$MTUI_CONFIG" \
      < /dev/null > "$LOG_FILE" 2>&1 &
  else
    python -m manga_translator local -v -i "$SRC_DIR" -o "$OUT_DIR" \
      --config-file "$CONFIG_FILE" \
      --font-path fonts/NotoSansCJKsc-Regular.otf \
      --use-gpu > "$LOG_FILE" 2>&1 &
  fi
  CHILD_PID=$!
  printf "%s\n" "$CHILD_PID" > "${PID_FILE}.tmp" && mv -f "${PID_FILE}.tmp" "$PID_FILE"
  wait "$CHILD_PID"
  RC=$?

  if [ "$ENGINE" = "mtui" ]; then
    # mtui exits 0 even when pages failed; rewrite rc from the closing summary (失败: N).
    if [ "$RC" -eq 0 ]; then
      FAIL_COUNT=$(grep -aoE "失败: [0-9]+[[:space:]]*$" "$LOG_FILE" | tail -n 1 | grep -oE "[0-9]+" || true)
      if [ -n "$FAIL_COUNT" ] && [ "$FAIL_COUNT" -gt 0 ]; then
        RC=3
      fi
    fi
    # Output dir convention check: empty expected dir + images found elsewhere under
    # the output root means the layout differed from what out_path promised.
    if [ "$RC" -eq 0 ]; then
      EXPECTED_IMAGES=$(find "$REAL_OUT_DIR" -maxdepth 1 -type f ! -name ".*" \
        \( -iname "*.jpg" -o -iname "*.jpeg" -o -iname "*.png" -o -iname "*.webp" -o -iname "*.bmp" -o -iname "*.tif" -o -iname "*.tiff" \) \
        2>/dev/null | wc -l | tr -d " ")
      if [ "$EXPECTED_IMAGES" -le 0 ]; then
        if find "$ENGINE_OUT_DIR" -type f \
          \( -iname "*.jpg" -o -iname "*.jpeg" -o -iname "*.png" -o -iname "*.webp" -o -iname "*.bmp" -o -iname "*.tif" -o -iname "*.tiff" \) \
          -print -quit 2>/dev/null | grep -q .; then
          echo "OUT_PATH_MISMATCH expected=$REAL_OUT_DIR" >> "$LOG_FILE"
          RC=4
        fi
      fi
    fi
  fi

  printf "%s\n" "$RC" > "${EXIT_FILE}.tmp" && mv -f "${EXIT_FILE}.tmp" "$EXIT_FILE"
' _ "$ENGINE" "$JOB_DIR" "$SRC_DIR" "$OUT_DIR" "$ENGINE_OUT_DIR" "$REAL_OUT_DIR" \
  "$CONFIG_FILE" "$MTUI_DIR" "$JOB_MTUI_CONFIG" "$MIT_DIR" < /dev/null >/dev/null 2>&1 &
WRAPPER_PID=$!

# Briefly wait for CHILD_PID file to appear so callers get the real python PID when possible.
REPORTED_PID="$WRAPPER_PID"
for _ in 1 2 3 4 5 6 7 8 9 10; do
  if [ -s "$PID_FILE" ]; then
    REPORTED_PID=$(cat "$PID_FILE" 2>/dev/null | tr -d '[:space:]')
    break
  fi
  sleep 0.1
done

echo "PROTO 2"
echo "ENGINE $ENGINE"
echo "IMAGES $ACTUAL_COUNT"
echo "TRANSLATE_PID $REPORTED_PID"
