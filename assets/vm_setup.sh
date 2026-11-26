#!/bin/bash
# GUI-owned idempotent VM setup for MangaGui (PROTO 2): translation engine + llama.cpp + Sakura 14B + services.
# Derived from ../vm_setup.sh (left untouched for the command-line workflow). Differences:
#   - engine selection: `vm_setup.sh <mit|mtui>` or `--detach <engine>`; bare / `--detach` defaults to mit
#   - every stage stops at its first failing command instead of reporting the last command's status
#   - downloads go to "<file>.part", must succeed over HTTP and reach a minimum size before being renamed
#   - one instance at a time (flock), so a retry cannot race a still-running earlier run
#   - the mit API server (:8001) is optional: the GUI translates with `manga_translator local`
#   - on success writes /content/engine_ready (ENGINE=<engine>, REV=<commit|mit>)
# Safe to re-run; completed stages are skipped.
set -uo pipefail

# --- GUI polling modes: --detach [engine] / --poll <offset> ---
# The GUI cannot hold one ssh open for the whole deploy: a dropped link kills the run while
# billing continues. --detach starts the setup detached so a reconnect can re-attach;
# --poll reports newly appended bytes plus the exit code.
validate_args() {
  ENGINE=${1:-mit}; PROFILE_HINT=${2:-auto}; INPAINT_MODE=${3:-lama_large}
  case "$ENGINE" in mit|mtui) ;; *) echo "UNKNOWN_ENGINE $ENGINE"; return 2 ;; esac
  case "$PROFILE_HINT" in auto|l4_24g|a100_40g|gpu_80g|g4_96g|g4_96g_balanced) ;; *) echo "UNKNOWN_PROFILE $PROFILE_HINT"; return 2 ;; esac
  case "$INPAINT_MODE" in lama_large|flux2-klein) ;; *) echo "UNKNOWN_INPAINT_MODE $INPAINT_MODE"; return 2 ;; esac
  if [ "$#" -gt 3 ]; then echo 'TOO_MANY_SETUP_ARGUMENTS'; return 2; fi
}
if [ "${1:-}" = "--detach" ]; then
  shift
  validate_args "$@" || exit $?
  if ! flock -n /content/.vm_setup.lock true 2>/dev/null; then
    echo "SETUP_ALREADY_RUNNING"
    exit 0
  fi
  rm -f /content/vm_setup.rc
  : > /content/vm_setup.out
  setsid nohup bash -c 'bash /content/vm_setup.sh "$@"; echo $? > /content/vm_setup.rc' _ "$ENGINE" "$PROFILE_HINT" "$INPAINT_MODE" \
    >> /content/vm_setup.out 2>&1 < /dev/null 9>&- &
  echo "SETUP_DETACHED $ENGINE"
  exit 0
fi

if [ "${1:-}" = "--poll" ]; then
  offset="${2:-0}"
  case "$offset" in ''|*[!0-9]*) offset=0 ;; esac
  maxbytes="${3:-131072}"
  case "$maxbytes" in ''|*[!0-9]*) maxbytes=131072 ;; esac
  size=$(stat -c%s /content/vm_setup.out 2>/dev/null || echo 0)
  end="$size"
  if [ $((offset + maxbytes)) -lt "$end" ]; then
    end=$((offset + maxbytes))
  fi
  echo "__SIZE=$size"
  echo "__NEXT=$end"
  echo "__BEGIN"
  if [ "$end" -gt "$offset" ]; then
    tail -c +$((offset + 1)) /content/vm_setup.out | head -c $((end - offset))
  fi
  echo
  echo "__END"
  if [ -f /content/vm_setup.rc ]; then
    echo "__RC=$(cat /content/vm_setup.rc)"
  else
    echo "__RC="
  fi
  if flock -n /content/.vm_setup.lock true 2>/dev/null; then
    echo "__RUNNING=0"
  else
    echo "__RUNNING=1"
  fi
  exit 0
fi

validate_args "$@" || exit $?

MIT_DIR=/content/manga-image-translator
FONT="$MIT_DIR/fonts/NotoSansCJKsc-Regular.otf"
BUILD=b12728
SAKURA=/content/sakura14b.gguf
HF=https://huggingface.co
MTUI_DIR=/content/mtui
MTUI_REV=8cd6feec33a66ace50042f75676965eb3d4d8a10
MTUI_MARKER="$MTUI_DIR/.gui_setup_ok"
UV_VERSION=0.13.8

mark() { echo; echo "===== $1 ====="; }

# fetch <url> <dest> <min_bytes>: download completely, verify size, then rename into place.
fetch() {
  local url="$1" dest="$2" min="$3" size
  if [ -f "$dest" ] && [ "$(stat -c%s "$dest")" -ge "$min" ]; then return 0; fi
  mkdir -p "$(dirname "$dest")" || return 1
  if command -v aria2c >/dev/null; then
    aria2c -x 16 -s 16 -k 1M --file-allocation=none --continue=true --auto-file-renaming=false \
      --allow-overwrite=true --max-tries=3 --retry-wait=5 --summary-interval=15 \
      -d "$(dirname "$dest")" -o "$(basename "$dest").part" "$url" || return 1
  else
    curl -fL --retry 3 --retry-delay 5 -sS -o "${dest}.part" "$url" || return 1
  fi
  size=$(stat -c%s "${dest}.part" 2>/dev/null || echo 0)
  if [ "$size" -lt "$min" ]; then
    echo "FETCH_TOO_SMALL $dest size=$size min=$min"
    return 1
  fi
  mv -f "${dest}.part" "$dest"
}

# retry <cmd...>: transient network failures get two more attempts.
retry() {
  local n=0
  until "$@"; do
    n=$((n + 1))
    if [ "$n" -ge 3 ]; then
      echo "RETRY_EXHAUSTED $*"
      return 1
    fi
    echo "retrying ($n/3): $*"
    sleep 10
  done
}

mtui_installed() {
  [ -f "$MTUI_MARKER" ] && [ "$(cat "$MTUI_MARKER" 2>/dev/null)" = "$MTUI_REV cuda13.0" ]
}

# The lock lives on fd 9. Long-running services are started with "9>&-" so they do not inherit it;
# otherwise they would keep the lock held after this script exits.
exec 9>/content/.vm_setup.lock
if ! flock -n 9; then
  echo "another vm_setup.sh is still running, waiting for it to finish..."
  if ! flock -w 1500 9; then
    echo "SETUP_BUSY previous vm_setup.sh did not finish within 25 minutes"
    exit 75
  fi
fi

# Mark this VM as having run a new-protocol deploy: vm_status.sh/vm_job.sh only
# infer a legacy (v1.3.x) mit engine when this file is absent, so a failed or
# in-progress new deploy is never mistaken for a working old install.
: > /content/.gui_engine_proto

# A (re)deploy may switch engines; drop the marker until the new deploy completes.
rm -f /content/engine_ready

mark "STAGE0 probe GPU and resolve profile"
GPU_INFO=$(nvidia-smi --query-gpu=name,memory.total,compute_cap --format=csv,noheader,nounits 2>/dev/null | head -n1)
IFS=, read -r GPU_NAME GPU_VRAM_MB COMPUTE_CAP <<< "$GPU_INFO"
GPU_NAME=${GPU_NAME:-unknown}; GPU_VRAM_MB=${GPU_VRAM_MB//[[:space:]]/}; COMPUTE_CAP=${COMPUTE_CAP//[[:space:]]/}
if ! [[ "$GPU_VRAM_MB" =~ ^[1-9][0-9]*$ ]]; then GPU_VRAM_MB=23034; echo 'WARN GPU probing failed, assuming L4'; fi
if (( GPU_VRAM_MB < 30000 )); then actual=l4_24g
elif (( GPU_VRAM_MB < 60000 )); then actual=a100_40g
elif (( GPU_VRAM_MB < 90000 )); then actual=gpu_80g
else actual=g4_96g; fi
case "$PROFILE_HINT" in
  auto) GPU_PROFILE=$actual; required=0 ;;
  l4_24g) GPU_PROFILE=$PROFILE_HINT; required=0 ;;
  a100_40g) GPU_PROFILE=$PROFILE_HINT; required=30000 ;;
  gpu_80g|g4_96g_balanced) GPU_PROFILE=$PROFILE_HINT; required=60000 ;;
  g4_96g) GPU_PROFILE=$PROFILE_HINT; required=90000 ;;
esac
if [ "$ENGINE" = mit ]; then GPU_PROFILE=l4_24g
elif (( GPU_VRAM_MB < required )); then
  echo "WARN $PROFILE_HINT requires >= $required MiB, found $GPU_VRAM_MB; using $actual"
  GPU_PROFILE=$actual
fi
if [ "$GPU_PROFILE" = l4_24g ]; then INPAINT_MODE=lama_large; fi
echo "GPU_PROFILE $GPU_PROFILE ($GPU_NAME, $GPU_VRAM_MB MiB); INPAINT_MODE $INPAINT_MODE"
if ! command -v aria2c >/dev/null; then
  (set -e; retry apt-get update -qq; retry apt-get install -y -qq aria2) >/content/aria2_install.log 2>&1
  if [ $? -ne 0 ]; then echo 'WARN aria2 install failed, using curl'; fi
fi

mark "STAGE1 translation engine ($ENGINE)"
if [ "$ENGINE" = "mit" ]; then
  if [ -d "$MIT_DIR" ] && [ -s "$FONT" ]; then
    echo "mit already installed, skip"
  else
    # Standalone subshell (not part of an && / || list) so that `set -e` is honoured inside it.
    (
      set -e
      if [ ! -d "$MIT_DIR/.git" ]; then
        rm -rf "$MIT_DIR"
        git clone --depth 1 https://github.com/zyddnys/manga-image-translator "$MIT_DIR"
      fi
      cd "$MIT_DIR"
      pip install -q -r requirements.txt
      pip install -q torch torchvision --force-reinstall --no-deps --index-url https://download.pytorch.org/whl/cu124
      pip install -q torchaudio==2.6.0 --index-url https://download.pytorch.org/whl/cu124
      pip install -q nvidia-cusparselt-cu12
      # The font is fetched last on purpose: its presence marks the whole stage as complete.
      fetch https://github.com/notofonts/noto-cjk/raw/main/Sans/OTF/SimplifiedChinese/NotoSansCJKsc-Regular.otf "$FONT" 5000000
    ) > /content/env.log 2>&1
    if [ $? -eq 0 ]; then
      echo "STAGE1_OK"
    else
      echo "STAGE1_FAIL"; tail -c 800 /content/env.log; exit 1
    fi
  fi
else
  if mtui_installed; then
    echo "mtui already installed, skip"
  else
    (
      set -e
      retry apt-get update -qq
      retry apt-get install -y -qq libegl1 libxkbcommon0 libgl1 libfontconfig1 libdbus-1-3 p7zip-full
      retry pip install -q "uv==$UV_VERSION"
      if [ ! -d "$MTUI_DIR/.git" ]; then
        rm -rf "$MTUI_DIR"
        mkdir -p "$MTUI_DIR"
        git -C "$MTUI_DIR" init -q
        git -C "$MTUI_DIR" remote add origin https://github.com/hgmzhn/manga-translator-ui.git
      fi
      retry git -C "$MTUI_DIR" fetch -q --depth 1 origin "$MTUI_REV"
      git -C "$MTUI_DIR" checkout -q FETCH_HEAD
      python3 /content/mtui_patch.py "$MTUI_DIR"
      retry uv python install 3.12
      cd "$MTUI_DIR"
      retry uv venv --clear -p 3.12 .venv
      retry uv sync --frozen --no-default-groups --group cuda13.0
      .venv/bin/python -c "import torch; assert torch.cuda.is_available(), 'torch.cuda not available'" < /dev/null
      QT_QPA_PLATFORM=offscreen .venv/bin/python -c "from PyQt6.QtGui import QGuiApplication; QGuiApplication([])" < /dev/null
      echo "$MTUI_REV cuda13.0" > "$MTUI_MARKER"
    ) > /content/env.log 2>&1
    if [ $? -eq 0 ]; then
      echo "STAGE1_OK"
    else
      echo "STAGE1_FAIL"; tail -c 800 /content/env.log; exit 1
    fi
  fi
fi

mark "STAGE2 llama.cpp + model"
SAKURA_URL="$HF/SakuraLLM/Sakura-GalTransl-14B-v3.8/resolve/main/Sakura-Galtransl-14B-v3.8.gguf"
FALLBACK_PORT=; CONTEXT_SIZE=3; MMPROJ=; PRIMARY_FLAGS=(-c 16384 -np 1 -fa on -ngl 999 --cache-type-k q8_0 --cache-type-v q8_0)
case "$GPU_PROFILE" in
  l4_24g)
    MODEL=$SAKURA; PRIMARY_MODEL=sakura-galtransl-14b; PRIMARY_FLAGS=(-c 16384 -np 8 -fa on -ngl 999) ;;
  a100_40g)
    repo=Qwen3.5-27B-GGUF; MODEL=/content/models_27b/Qwen3.5-27B-Q6_K.gguf
    min_model=20000000000; min_proj=700000000; PRIMARY_MODEL=qwen35-27b; CONTEXT_SIZE=5
    PRIMARY_FLAGS+=(--reasoning-budget 0) ;;
  gpu_80g|g4_96g_balanced)
    repo=Qwen3-VL-32B-Instruct-GGUF; MODEL=/content/models_32b/Qwen3-VL-32B-Instruct-Q8_0.gguf
    min_model=30000000000; min_proj=900000000; PRIMARY_MODEL=qwen3-vl-32b; CONTEXT_SIZE=8; FALLBACK_PORT=6007
    if [ "$GPU_PROFILE" = g4_96g_balanced ]; then CONTEXT_SIZE=10; fi ;;
  g4_96g)
    repo=Qwen3.5-122B-A10B-GGUF; shard=Qwen3.5-122B-A10B-UD-Q4_K_XL
    MODEL="/content/models_122b/$shard-00001-of-00003.gguf"; min_proj=700000000
    PRIMARY_MODEL=qwen35-122b; CONTEXT_SIZE=6; PRIMARY_FLAGS+=(--reasoning-budget 0) ;;
esac
if [ "$GPU_PROFILE" != l4_24g ]; then
  model_url="$HF/unsloth/$repo/resolve/main"; MMPROJ="${MODEL%/*}/mmproj-BF16.gguf"; PRIMARY_FLAGS+=(--mmproj "$MMPROJ")
fi
(
  set -e
  if [ "$GPU_PROFILE" = l4_24g ] || [ -n "$FALLBACK_PORT" ]; then fetch "$SAKURA_URL" "$SAKURA" 8000000000; fi
  if [ -n "$MMPROJ" ]; then
    fetch "$model_url/mmproj-BF16.gguf" "$MMPROJ" "$min_proj"
    if [ "$GPU_PROFILE" = g4_96g ]; then
      for i in 00001 00002 00003; do
        f="$shard-$i-of-00003.gguf"; fetch "$model_url/UD-Q4_K_XL/$f" "/content/models_122b/$f" 1000000000
      done
    else fetch "$model_url/${MODEL##*/}" "$MODEL" "$min_model"; fi
  fi
) > /content/model.log 2>&1
if [ $? -ne 0 ]; then echo 'STAGE2_FAIL'; tail -c 800 /content/model.log; exit 1; fi

# Keep CUDA builds separate so switching builds cannot mix incompatible libraries.
BASE_LD=${LD_LIBRARY_PATH:-}
CUSPARSELT_LIB=$(ls -d /usr/local/lib/python3*/dist-packages/nvidia/cusparselt/lib 2>/dev/null | head -n1)
prepare_build() {
  local cuda=$1 release="https://github.com/ggml-org/llama.cpp/releases/download/$BUILD" pkg dir
  LLAMA_DIR="/content/llama-$BUILD-cuda-$cuda"; local runtime="$LLAMA_DIR/runtime"
  for pkg in llama cudart-llama; do
    if [ "$pkg" = llama ]; then dir=$LLAMA_DIR; else dir=$runtime; fi
    if [ ! -f "$dir/.extracted" ]; then
      mkdir -p "$dir" || return 1
      local name="$pkg-$BUILD-bin-ubuntu-cuda-$cuda-x64.tar.gz"
      fetch "$release/$name" "$dir/$name" 1000000 && tar xzf "$dir/$name" --strip-components=1 -C "$dir" && touch "$dir/.extracted" || return 1
    fi
  done
  LLAMA_LD="$LLAMA_DIR:$runtime:${CUSPARSELT_LIB:-/usr/lib64-nvidia}:/usr/lib64-nvidia${BASE_LD:+:$BASE_LD}"
  LD_LIBRARY_PATH="$LLAMA_LD" "$LLAMA_DIR/llama-server" --version </dev/null
}
cuda=12.8
if ! prepare_build "$cuda" >> /content/model.log 2>&1; then
  if [ "$COMPUTE_CAP" != 12.0 ]; then echo 'STAGE2_FAIL'; tail -c 800 /content/model.log; exit 1; fi
  cuda=13.5; echo "WARN retrying Blackwell with CUDA $cuda"
  prepare_build "$cuda" >> /content/model.log 2>&1 || { echo 'STAGE2_FAIL'; tail -c 800 /content/model.log; exit 1; }
fi
echo 'STAGE2_OK'

mark "STAGE3 env.sh"
write_env() { printf 'export LD_LIBRARY_PATH=%q\n' "$LLAMA_LD" > /content/env.sh; }
write_env || exit 1

mark "STAGE4 llama-server"
# Only manage llama-server binaries installed by setup with an explicit --port.
managed_pids() {
  local pid exe i argv
  for pid in $(pgrep -x llama-server); do
    exe=$(readlink "/proc/$pid/exe") || continue
    [[ "$exe" == /content/llama-*/llama-server ]] || continue
    mapfile -d '' -t argv < "/proc/$pid/cmdline" || continue
    for ((i=1; i<${#argv[@]}-1; i++)); do
      if [[ ${argv[i]} == --port && ${argv[i+1]} == "$1" ]]; then printf '%s\n' "$pid"; break; fi
    done
  done
}
stop_server() {
  local pid i
  for pid in $(managed_pids "$1"); do
    kill -TERM "$pid" 2>/dev/null || continue
    for ((i=0; i<50; i++)); do [ -L "/proc/$pid/exe" ] || break; sleep .2; done
    if readlink "/proc/$pid/exe" >/dev/null; then echo "LLAMA_STOP_TIMEOUT port=$1 pid=$pid"; return 1; fi
  done
}
serves() {
  curl -fsS -m 3 "http://127.0.0.1:$1/health" 2>/dev/null | grep -q ok &&
    curl -fsS -m 3 "http://127.0.0.1:$1/v1/models" 2>/dev/null | python3 -c 'import json,sys; sys.exit(not any(m.get("id")==sys.argv[1] for m in json.load(sys.stdin)["data"]))' "$2"
}
start_server() {
  local port=$1 alias=$2 model=$3 log=$4 pid i; shift 4
  for pid in $(managed_pids "$port"); do
    if [ "$(readlink "/proc/$pid/exe")" = "$LLAMA_DIR/llama-server" ] && serves "$port" "$alias"; then echo "$alias already up on $port"; return 0; fi
  done
  stop_server "$port" || return 1
  LD_LIBRARY_PATH="$LLAMA_LD" nohup "$LLAMA_DIR/llama-server" -m "$model" "$@" --host 127.0.0.1 --port "$port" -a "$alias" \
    > "$log" 2>&1 < /dev/null 9>&- &
  pid=$!
  # Loading the 77GB MoE shards takes longer than the old Sakura timeout.
  for ((i=0; i<120; i++)); do
    kill -0 "$pid" 2>/dev/null || break
    if serves "$port" "$alias" && kill -0 "$pid" 2>/dev/null; then return 0; fi
    sleep 3
  done
  stop_server "$port"; tail -c 800 "$log"; return 1
}
start_services() {
  start_server 6006 "$PRIMARY_MODEL" "$MODEL" /content/llama.log "${PRIMARY_FLAGS[@]}" || return 1
  if [ -n "$FALLBACK_PORT" ]; then
    start_server "$FALLBACK_PORT" sakura-galtransl-14b "$SAKURA" /content/llama_fallback.log -c 8192 -np 2 -fa on -ngl 999 || return 1
  fi
}
if [ -z "$FALLBACK_PORT" ]; then stop_server 6007 || exit 1; fi
if [ "$ENGINE" = mtui ]; then pkill -f '[s]erver/main.py --host 127.0.0.1 --port 8001' 2>/dev/null || true; fi
if ! start_services; then
  if [[ "$cuda" != 12.8 || "$COMPUTE_CAP" != 12.0 ]]; then echo 'LLAMA_FAIL'; exit 1; fi
  echo 'WARN retrying Blackwell startup with CUDA 13.5'
  stop_server 6007 && stop_server 6006 && prepare_build 13.5 >> /content/model.log 2>&1 && write_env && start_services || { echo 'LLAMA_FAIL'; exit 1; }
fi
echo 'LLAMA_UP'

MIT_STATE="down"
MTUI_STATE="down"
mark "STAGE5 warm up translation engine ($ENGINE)"
if [ "$ENGINE" = "mit" ]; then
  if [ "$(curl -s -m 3 -o /dev/null -w '%{http_code}' http://127.0.0.1:8001/docs 2>/dev/null)" = "200" ]; then
    echo "mit server already up"
    MIT_STATE="up"
  else
    source /content/env.sh
    export SAKURA_API_BASE=http://127.0.0.1:6006/v1
    export SAKURA_VERSION=0.10
    (cd "$MIT_DIR" && nohup python server/main.py --host 127.0.0.1 --port 8001 --use-gpu --start-instance \
      > /content/mitserver.log 2>&1 9>&- &)
    for _ in $(seq 1 20); do
      sleep 3
      if [ "$(curl -s -m 3 -o /dev/null -w '%{http_code}' http://127.0.0.1:8001/docs 2>/dev/null)" = "200" ]; then
        MIT_STATE="up"; break
      fi
    done
    if [ "$MIT_STATE" = "up" ]; then
      echo "MIT_UP"
    else
      # Not fatal: the GUI pipeline does not use this server.
      echo "MIT_SKIPPED mit api server did not come up (optional)"; tail -c 300 /content/mitserver.log 2>/dev/null
    fi
  fi
else
  export MPLBACKEND=Agg
  ACTIVE=/content/mtui_active_config.json
  if [ "$GPU_PROFILE" = l4_24g ]; then
    cp /content/mtui_config.json "$ACTIVE" || exit 1
  else
    python3 - /content/mtui_config.json "$ACTIVE" "$INPAINT_MODE" "$CONTEXT_SIZE" <<'PY' || exit 1
import json, sys
from pathlib import Path
src, dest, mode, context = sys.argv[1:]
config = json.loads(Path(src).read_text(encoding='utf-8'))
config['translator'].update(translator='openai_hq', enable_streaming=True, extract_glossary=True,
                            enable_post_translation_check=True)
config['ocr']['ocr'] = 'hayai_ocr_v2'
config['inpainter']['inpainter'] = mode
config['cli']['context_size'] = int(context)
Path(dest).write_text(json.dumps(config, ensure_ascii=False, indent=2), encoding='utf-8')
PY
    fetch "$HF/JustANormalTinkerer/hayai-ocr-v2/resolve/4a4ce477c9a8841f208b94e1d9ed5c0938965e05/model.safetensors" \
      "$MTUI_DIR/models/ocr/hayai-ocr-v2/model.safetensors" 500000000 || { echo 'STAGE5_FAIL hayai prefetch'; exit 1; }
    if [ "$INPAINT_MODE" = flux2-klein ]; then
      f=flux-2-klein-4b-Q4_K_M.gguf
      fetch "$HF/unsloth/FLUX.2-klein-4B-GGUF/resolve/main/$f" "$MTUI_DIR/models/inpainting/flux2-klein/transformer/$f" 2000000000 || { echo 'STAGE5_FAIL flux prefetch'; exit 1; }
    fi
  fi

  # Pre-download every model the mtui config uses so the first job never waits on
  # downloads, and fail the deploy here (not at first job) when any model is missing.
  cat > /tmp/mtui_prepare.py <<'PYEOF'
import asyncio, json, sys
sys.path.insert(0, '/content/mtui')

async def main():
    from manga_translator.config import Detector, Ocr, Inpainter
    from manga_translator.detection import get_detector
    from manga_translator.detection.yolo_obb import YOLOOBBDetector
    from manga_translator.ocr import get_ocr
    from manga_translator.inpainting import get_inpainter
    from manga_translator.utils.mangalens_detector import MangaLensBubbleDetector
    from manga_translator.rendering.chinese_linebreak import download_chinese_linebreak_models

    results = []
    async def dl(name, model):
        await model.download()
        ok = model.is_downloaded()
        print(('MODEL_OK' if ok else 'MODEL_MISSING'), name, flush=True)
        results.append(ok)

    await dl('detector:default', get_detector(Detector.default))
    await dl('detector:yolo_obb', YOLOOBBDetector())
    await dl('detector:mangalens', MangaLensBubbleDetector(auto_load=False))
    with open('/content/mtui_active_config.json', encoding='utf-8') as f:
        config = json.load(f)
    ocr = config['ocr']['ocr']
    inpainter = config['inpainter']['inpainter']
    await dl('ocr:' + ocr, get_ocr(Ocr(ocr)))
    await dl('inpainter:' + inpainter, get_inpainter(Inpainter(inpainter)))
    if config['translator']['translator'] == 'openai_hq':
        from manga_translator.config import Upscaler
        from manga_translator.upscaling import get_upscaler
        await dl('upscale:mangajanai:x2', get_upscaler(Upscaler.mangajanai, model_name='x2'))

    ok = await download_chinese_linebreak_models()
    print(('MODEL_OK' if ok else 'MODEL_MISSING'), 'render:chinese_linebreak', flush=True)
    results.append(ok)

    if not all(results):
        print('MODELS_MISSING', flush=True)
        sys.exit(1)
    print('MODELS_READY', flush=True)

asyncio.run(main())
PYEOF
  ( cd "$MTUI_DIR" && QT_QPA_PLATFORM=offscreen .venv/bin/python /tmp/mtui_prepare.py < /dev/null ) > /content/mtui_prepare.log 2>&1
  if [ $? -eq 0 ] && grep -q "MODELS_READY" /content/mtui_prepare.log; then
    MTUI_STATE="ready"
    echo "MTUI_READY"
  else
    MTUI_STATE="fail"
    echo "STAGE5_FAIL mtui model preload failed; switch to the mit engine in settings and retry deploy"
    tail -c 1200 /content/mtui_prepare.log
    exit 1
  fi
fi

mark "SUMMARY"
echo "engine: $ENGINE"
echo "llama:  $(curl -s -m 3 http://127.0.0.1:6006/health 2>/dev/null)"
echo "mit:    $MIT_STATE (optional, :8001)"
echo "mtui:   $MTUI_STATE"
echo "gpu:    $(nvidia-smi --query-gpu=name,memory.used,memory.total --format=csv,noheader 2>/dev/null)"
if [ "$ENGINE" = "mit" ]; then
  REV_OUT="mit"
else
  REV_OUT="$MTUI_REV"
fi
printf 'ENGINE=%s\nREV=%s\nGPU_PROFILE=%s\nGPU_NAME=%s\nGPU_VRAM_MB=%s\nINPAINT_MODE=%s\nPRIMARY_MODEL=%s\nFALLBACK_PORT=%s\n' \
  "$ENGINE" "$REV_OUT" "$GPU_PROFILE" "$GPU_NAME" "$GPU_VRAM_MB" "$INPAINT_MODE" "$PRIMARY_MODEL" "$FALLBACK_PORT" > /content/engine_ready.tmp || exit 1
mv -f /content/engine_ready.tmp /content/engine_ready || exit 1
echo "ENGINE $ENGINE ready"
echo "VM_SETUP_COMPLETE"
