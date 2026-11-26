#!/bin/bash
# Task-isolated artifact packer, job stopper, log reader, and cleaner (PROTO 2).
# Usage:
#   vm_pack.sh pack <job_dir> [partial]
#   vm_pack.sh split_out <job_dir> <chunk_bytes>
#   vm_pack.sh join_parts <job_dir> <part_count> <expected_bytes> <expected_sha256>
#   vm_pack.sh stop_job <job_dir>
#   vm_pack.sh clean <job_dir>
#   vm_pack.sh clean_stale
#   vm_pack.sh tail_log <job_dir> [lines]
# MANGA_CONTENT_ROOT overrides the /content root for offline tests only.
set -euo pipefail

CONTENT_ROOT="${MANGA_CONTENT_ROOT:-/content}"
CMD="${1:-}"
JOB_DIR="${2:-}"

validate_job_dir() {
  local dir="$1"
  if [[ -z "$dir" || "$dir" != "$CONTENT_ROOT"/jobs/job_* || "$dir" == *".."* ]]; then
    echo "PACK_ERROR unsafe_job_dir $dir" >&2
    exit 1
  fi
}

terminate_pid() {
  local pid="$1"
  if [[ "$pid" =~ ^[0-9]+$ ]] && kill -0 "$pid" 2>/dev/null; then
    kill -TERM "$pid" 2>/dev/null || true
    pkill -TERM -P "$pid" 2>/dev/null || true
    for _ in 1 2 3 4 5 6 7 8 9 10; do
      if ! kill -0 "$pid" 2>/dev/null; then
        break
      fi
      sleep 0.2
    done
    if kill -0 "$pid" 2>/dev/null; then
      kill -KILL "$pid" 2>/dev/null || true
      pkill -KILL -P "$pid" 2>/dev/null || true
      sleep 0.3
    fi
    if kill -0 "$pid" 2>/dev/null; then
      return 1
    fi
  fi
  return 0
}

case "$CMD" in
  pack)
    validate_job_dir "$JOB_DIR"
    # The launcher records the real output image dir (engine-specific) in out_path.
    OUT_DIR=$(cat "$JOB_DIR/out_path" 2>/dev/null || true)
    if [ -z "$OUT_DIR" ]; then
      OUT_DIR="$JOB_DIR/out"
    fi
    OUT_ZIP="$JOB_DIR/out.zip"
    TMP_ZIP="$JOB_DIR/out.zip.tmp"
    PID_FILE="$JOB_DIR/pid"
    EXIT_FILE="$JOB_DIR/exit_code"
    # "partial" packs whatever pages an unfinished or failed job produced; the job must still be stopped.
    PACK_MODE="${3:-}"

    if [ ! -d "$OUT_DIR" ]; then
      echo "PACK_ERROR missing_dir $OUT_DIR" >&2
      exit 1
    fi

    if [ -s "$PID_FILE" ]; then
      PID=$(cat "$PID_FILE" 2>/dev/null | tr -d '[:space:]')
      if [[ "$PID" =~ ^[0-9]+$ ]] && kill -0 "$PID" 2>/dev/null; then
        echo "PACK_ERROR job_still_running pid=$PID" >&2
        exit 1
      fi
    fi

    if [ "$PACK_MODE" != "partial" ]; then
      if [ ! -s "$EXIT_FILE" ]; then
        echo "PACK_ERROR missing_exit_code" >&2
        exit 1
      fi

      RC=$(cat "$EXIT_FILE" 2>/dev/null | tr -d '[:space:]')
      if [ "$RC" != "0" ]; then
        echo "PACK_ERROR nonzero_exit_code $RC" >&2
        exit 1
      fi
    fi

    mapfile -t IMAGES < <(cd "$OUT_DIR" && find . -maxdepth 1 -type f ! -name '.*' \
      \( -iname '*.jpg' -o -iname '*.jpeg' -o -iname '*.png' -o -iname '*.webp' -o -iname '*.bmp' -o -iname '*.tif' -o -iname '*.tiff' \) \
      -printf '%P\n' | LC_ALL=C sort)

    IMG_COUNT="${#IMAGES[@]}"
    if [ "$IMG_COUNT" -le 0 ]; then
      echo "PACK_ERROR no_images $OUT_DIR" >&2
      exit 1
    fi

    rm -f "$TMP_ZIP" "$OUT_ZIP"
    (cd "$OUT_DIR" && zip -q "$TMP_ZIP" -- "${IMAGES[@]}")
    mv -f "$TMP_ZIP" "$OUT_ZIP"

    SIZE=$(stat -c%s "$OUT_ZIP")
    SHA256=$(sha256sum "$OUT_ZIP" | awk '{print $1}')
    echo "PACK_OK $SIZE $SHA256 $IMG_COUNT"
    ;;

  split_out)
    validate_job_dir "$JOB_DIR"
    OUT_ZIP="$JOB_DIR/out.zip"
    CHUNK_BYTES="${3:-0}"
    if [ ! -s "$OUT_ZIP" ]; then
      echo "SPLIT_ERROR missing_out_zip $OUT_ZIP" >&2
      exit 1
    fi
    if ! [[ "$CHUNK_BYTES" =~ ^[0-9]+$ ]] || [ "$CHUNK_BYTES" -le 0 ]; then
      echo "SPLIT_ERROR bad_chunk_bytes $CHUNK_BYTES" >&2
      exit 1
    fi
    rm -f "$OUT_ZIP".part_*
    split -b "$CHUNK_BYTES" -d -a 4 "$OUT_ZIP" "$OUT_ZIP.part_"
    PART_COUNT=$(find "$JOB_DIR" -maxdepth 1 -type f -name 'out.zip.part_*' | wc -l | tr -d ' ')
    echo "SPLIT_OK $PART_COUNT"
    ;;

  join_parts)
    validate_job_dir "$JOB_DIR"
    JOB_BASE=$(basename "$JOB_DIR")
    PART_COUNT="${3:-0}"
    EXPECTED_BYTES="${4:-}"
    EXPECTED_SHA="${5:-}"
    STAGED_ZIP="$CONTENT_ROOT/${JOB_BASE}.zip"
    if ! [[ "$PART_COUNT" =~ ^[0-9]+$ ]] || [ "$PART_COUNT" -le 0 ]; then
      echo "JOIN_ERROR bad_part_count $PART_COUNT" >&2
      exit 1
    fi
    rm -f "$STAGED_ZIP.tmp"
    for ((i = 0; i < PART_COUNT; i++)); do
      PART=$(printf '%s/%s.part_%04d' "$CONTENT_ROOT" "$JOB_BASE" "$i")
      if [ ! -f "$PART" ]; then
        echo "JOIN_ERROR missing_part $PART" >&2
        exit 1
      fi
      cat "$PART" >> "$STAGED_ZIP.tmp"
    done
    SIZE=$(stat -c%s "$STAGED_ZIP.tmp")
    SHA256=$(sha256sum "$STAGED_ZIP.tmp" | awk '{print $1}')
    if [ "$SIZE" != "$EXPECTED_BYTES" ] || [ "$SHA256" != "$EXPECTED_SHA" ]; then
      rm -f "$STAGED_ZIP.tmp"
      echo "JOIN_ERROR checksum_mismatch size=$SIZE sha256=$SHA256" >&2
      exit 1
    fi
    mv -f "$STAGED_ZIP.tmp" "$STAGED_ZIP"
    rm -f "$CONTENT_ROOT/$JOB_BASE".part_*
    echo "JOIN_OK $SIZE $SHA256"
    ;;

  stop_job)
    validate_job_dir "$JOB_DIR"
    PID_FILE="$JOB_DIR/pid"
    if [ -s "$PID_FILE" ]; then
      PID=$(cat "$PID_FILE" 2>/dev/null | tr -d '[:space:]')
      if ! terminate_pid "$PID"; then
        echo "STOP_JOB_STILL_ALIVE $PID" >&2
        exit 1
      fi
    fi
    echo "STOP_JOB_OK"
    ;;

  clean)
    if [[ -n "$JOB_DIR" && ( "$JOB_DIR" == "$CONTENT_ROOT"/jobs/job_* || "$JOB_DIR" == "$CONTENT_ROOT"/job_* ) && "$JOB_DIR" != *".."* ]]; then
      JOB_BASE=$(basename "$JOB_DIR")
      rm -rf "$JOB_DIR" "$CONTENT_ROOT/${JOB_BASE}.zip" "${JOB_DIR}.zip" "${JOB_DIR}_src" "${JOB_DIR}_out" "${JOB_DIR}_out.zip"
      rm -f "$CONTENT_ROOT/${JOB_BASE}.zip.tmp" "$CONTENT_ROOT/${JOB_BASE}".part_*
      echo "CLEAN_OK $JOB_DIR"
    else
      echo "CLEAN_SKIPPED unsafe_prefix" >&2
      exit 1
    fi
    ;;

  clean_stale)
    # Removes every stale job dir + staged zip left behind by a killed front-end,
    # and terminates orphaned translator processes still holding the GPU.
    # Only touches $CONTENT_ROOT/jobs and staged zips; never mtui/result or models.
    count_dirs=0
    for job_dir in "$CONTENT_ROOT"/jobs/job_*; do
      [ -d "$job_dir" ] || continue
      job_base=$(basename "$job_dir")
      pid_file="$job_dir/pid"
      if [ -s "$pid_file" ]; then
        stale_pid=$(cat "$pid_file" 2>/dev/null | tr -d '[:space:]')
        terminate_pid "$stale_pid" || true
      fi
      rm -rf "$job_dir" "$CONTENT_ROOT/${job_base}.zip" "${job_dir}.zip" "${job_dir}_src" "${job_dir}_out" "${job_dir}_out.zip"
      rm -f "$CONTENT_ROOT/${job_base}.zip.tmp" "$CONTENT_ROOT/${job_base}".part_*
      count_dirs=$((count_dirs + 1))
    done

    # The [m] keeps the pattern from matching this pgrep/pkill command itself.
    stale_pattern="[m]anga_translator local -i $CONTENT_ROOT/jobs/"
    count_procs=$(pgrep -f "$stale_pattern" 2>/dev/null | wc -l | tr -d ' ' || true)
    if [ "$count_procs" -gt 0 ]; then
      pkill -f "$stale_pattern" 2>/dev/null || true
      for _ in $(seq 1 50); do
        pgrep -f "$stale_pattern" >/dev/null 2>&1 || break
        sleep 0.2
      done
      pkill -KILL -f "$stale_pattern" 2>/dev/null || true
    fi
    echo "STALE_CLEAN_OK $count_dirs $count_procs"
    ;;

  tail_log)
    validate_job_dir "$JOB_DIR"
    LINES="${3:-100}"
    LOG_FILE="$JOB_DIR/translate.log"
    if [ -f "$LOG_FILE" ]; then
      tail -n "$LINES" "$LOG_FILE"
    else
      echo "(no $LOG_FILE)"
    fi
    ;;

  kill_translate)
    pkill -f "manga_translator local" 2>/dev/null || true
    echo "KILL_SENT"
    ;;

  *)
    echo "UNKNOWN_CMD $CMD" >&2
    exit 1
    ;;
esac
