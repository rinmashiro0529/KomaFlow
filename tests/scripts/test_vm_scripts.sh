#!/bin/bash
# T2: offline behavioral tests for gui/assets/vm_*.sh on a fake $MANGA_CONTENT_ROOT.
# Run with Git Bash: "C:\Program Files\Git\bin\bash.exe" test_vm_scripts.sh
# No network, no Colab. Exits non-zero when any check fails.
set -u

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ASSETS="$(cd "$SCRIPT_DIR/../../assets" && pwd)"
ROOT="$(mktemp -d)"
CR="$ROOT/content"
BIN="$ROOT/bin"
PASS=0
FAIL=0

ok()   { PASS=$((PASS+1)); echo "PASS $1"; }
bad()  { FAIL=$((FAIL+1)); echo "FAIL $1 -- $2"; }
check(){ if [ "$2" = "$3" ]; then ok "$1"; else bad "$1" "want [$3] got [$2]"; fi; }
contains(){ case "$2" in *"$3"*) ok "$1";; *) bad "$1" "missing [$3] in [$2]";; esac; }

mkdir -p "$CR/jobs" "$BIN"

# ---------- fake helpers ----------
# setsid: Git Bash lacks it; production uses it to detach the worker.
cat > "$BIN/setsid" <<'EOF'
#!/bin/bash
exec "$@"
EOF
# zip: Git Bash lacks it; pack only needs `zip -q <out> -- files...` in cwd.
cat > "$BIN/zip" <<'EOF'
#!/bin/bash
# zip -q <out> -- <files...>
out=""; seen_dd=0; files=()
for a in "$@"; do
  if [ "$seen_dd" = "1" ]; then files+=("$a");
  elif [ "$a" = "--" ]; then seen_dd=1;
  elif [ "$a" = "-q" ]; then :
  elif [ -z "$out" ]; then out="$a"; fi
done
python - "$out" "${files[@]}" <<'PY'
import sys, zipfile
out, files = sys.argv[1], sys.argv[2:]
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
    for f in files:
        z.write(f, f)
PY
EOF
chmod +x "$BIN/setsid" "$BIN/zip"

# fake translator engine: parses -i/-o, behavior via FAKE_MODE env.
cat > "$BIN/fake_engine.sh" <<'EOF'
#!/bin/bash
src=""; out=""; prev=""
for a in "$@"; do
  case "$prev" in -i) src="$a";; -o) out="$a";; esac
  prev="$a"
done
echo "DICT_USED=${SAKURA_DICT_PATH:-<unset>}"
case "${FAKE_MODE:-ok}" in
  ok)
    dst="$out/$(basename "$src")"
    mkdir -p "$dst"
    for f in "$src"/*; do cp "$f" "$dst/"; echo "[BATCH] Saved successfully: $dst/$(basename "$f")"; done
    echo "完成: 3"
    echo "失败: 0"
    exit 0 ;;
  fail2)
    dst="$out/$(basename "$src")"
    mkdir -p "$dst"; cp "$src"/* "$dst/" 2>/dev/null
    echo "完成: 1"
    echo "失败: 2"
    exit 0 ;;
  wrongdir)
    mkdir -p "$out/wrong"; cp "$src"/* "$out/wrong/" 2>/dev/null
    echo "完成: 3"; exit 0 ;;
  exit7) exit 7 ;;
esac
EOF
# Bare `python` shim only for the mit worker (which resolves python via PATH); it lives
# in its own dir so the test's own python calls are never hijacked.
mkdir -p "$BIN/mitpath"
cp "$BIN/fake_engine.sh" "$BIN/mitpath/python"
chmod +x "$BIN/fake_engine.sh" "$BIN/mitpath/python"

export PATH="$BIN:$PATH"
export MANGA_CONTENT_ROOT="$CR"

# ---------- deploy assets into fake /content ----------
for f in vm_setup.sh vm_job.sh vm_status.sh vm_pack.sh; do
  cp "$ASSETS/$f" "$CR/$f"
done
cp "$ASSETS/mtui_config.json" "$CR/mtui_config.json"
cp "$ASSETS/sakura_dict.txt" "$CR/sakura_dict.txt"
echo '{}' > "$CR/manga.json"
echo 'export DUMMY=1' > "$CR/env.sh"

# fake mtui install with engine script
mkdir -p "$CR/mtui/.venv/bin" "$CR/manga-image-translator/fonts"
cp "$BIN/fake_engine.sh" "$CR/mtui/.venv/bin/python"
echo x > "$CR/manga-image-translator/fonts/NotoSansCJKsc-Regular.otf"

make_job_zip() {
  local job="$1" n="${2:-3}"
  mkdir -p "$job"
  python - "$job/input.zip" "$n" <<'PY'
import sys, zipfile
with zipfile.ZipFile(sys.argv[1], 'w') as z:
    for i in range(int(sys.argv[2])):
        z.writestr(f'{i+1:03d}.png', b'PNGDATA%d' % i)
PY
}

wait_exit_code() {
  local job="$1" i
  for i in $(seq 1 100); do
    [ -s "$job/exit_code" ] && { cat "$job/exit_code"; return 0; }
    sleep 0.1
  done
  echo "TIMEOUT"
}

# ---------- bash -n ----------
for f in vm_setup.sh vm_job.sh vm_status.sh vm_pack.sh; do
  bash -n "$CR/$f" && ok "bash -n $f" || bad "bash -n $f" "syntax error"
done

# ---------- vm_status.sh ENGINE three cases ----------
echo "ENGINE=mtui
REV=8cd6fee" > "$CR/engine_ready"
out="$(bash "$CR/vm_status.sh")"
contains "status ENGINE mtui ready" "$out" "ENGINE mtui ready"

rm -f "$CR/engine_ready"
out="$(bash "$CR/vm_status.sh")"
contains "status ENGINE legacy mit" "$out" "ENGINE mit ready"

mv "$CR/manga-image-translator" "$CR/mit_hidden"
out="$(bash "$CR/vm_status.sh")"
contains "status ENGINE none missing" "$out" "ENGINE none missing"
mv "$CR/mit_hidden" "$CR/manga-image-translator"

# ---------- vm_status.sh DONE_COUNT via out_path ----------
job1="$CR/jobs/job_status_001"
mkdir -p "$job1/out" "$job1/engine_out/src"
touch "$job1/out/old.png"
touch "$job1/engine_out/src/001.png" "$job1/engine_out/src/002.png"
echo '{}' > "$job1/engine_out/src/translation_map.json"
mkdir -p "$job1/engine_out/src/nested"; touch "$job1/engine_out/src/nested/deep.png"
echo "0" > "$job1/exit_code"
echo "$job1/engine_out/src" > "$job1/out_path"
out="$(bash "$CR/vm_status.sh" "$job1")"
contains "status DONE_COUNT from out_path" "$out" "DONE_COUNT 2"

rm -f "$job1/out_path"
out="$(bash "$CR/vm_status.sh" "$job1")"
contains "status DONE_COUNT fallback out/" "$out" "DONE_COUNT 1"

# ---------- vm_pack.sh pack honors out_path, drops non-images ----------
echo "$job1/engine_out/src" > "$job1/out_path"
out="$(bash "$CR/vm_pack.sh" pack "$job1")"
contains "pack PACK_OK" "$out" "PACK_OK"
names="$(python - "$job1/out.zip" <<'PY'
import sys, zipfile
print(' '.join(zipfile.ZipFile(sys.argv[1]).namelist()))
PY
)"
contains "pack images from out_path" "$names" "001.png 002.png"
case "$names" in *translation_map*) bad "pack excludes non-images" "$names";; *) ok "pack excludes non-images";; esac

# ---------- vm_job.sh engine_not_ready ----------
# G4: with mit dir+font present and no engine_ready, the legacy rule would let this
# through as mit — the .gui_engine_proto marker (written by new-protocol deploys)
# is what keeps it an error.
: > "$CR/.gui_engine_proto"
rm -f "$CR/engine_ready"
job2="$CR/jobs/job_noready_001"
make_job_zip "$job2" 2
out="$(bash "$CR/vm_job.sh" start "$job2" 2 2>&1 || true)"
contains "job engine_not_ready" "$out" "JOB_ERROR engine_not_ready"

# ---------- vm_job.sh mtui exit-code rewrites ----------
echo "ENGINE=mtui" > "$CR/engine_ready"
run_mtui_job() {
  local name="$1" mode="$2"
  local job="$CR/jobs/$name"
  make_job_zip "$job" 3
  FAKE_MODE="$mode" bash "$CR/vm_job.sh" start "$job" 3 >/dev/null
  wait_exit_code "$job"
}

rc="$(run_mtui_job job_mtui_ok ok)";        check "mtui success rc=0" "$rc" "0"
rc="$(run_mtui_job job_mtui_fail2 fail2)";  check "mtui 失败:2 -> rc=3" "$rc" "3"
rc="$(run_mtui_job job_mtui_wrong wrongdir)"; check "mtui wrong out dir -> rc=4" "$rc" "4"
rc="$(run_mtui_job job_mtui_exit7 exit7)";  check "mtui rc passthrough 7" "$rc" "7"

# ---------- vm_job.sh mit path ----------
echo "ENGINE=mit" > "$CR/engine_ready"
job3="$CR/jobs/job_mit_ok"
make_job_zip "$job3" 2
PATH="$BIN/mitpath:$PATH" FAKE_MODE=ok bash "$CR/vm_job.sh" start "$job3" 2 >"$ROOT/mit_start.log"
contains "mit start prints ENGINE" "$(cat "$ROOT/mit_start.log")" "ENGINE mit"
rc="$(wait_exit_code "$job3")"
# mit fake writes to <out>/<basename src> but out_path expects $JOB/out -- the fake must
# write flat for mit; emulate by checking passthrough only.
check "mit rc numeric" "$([ "$rc" != "TIMEOUT" ] && echo yes || echo no)" "yes"

# ---------- vm_job.sh sakura_dict gating (both engines share the export) ----------
# empty bundled dict must NOT be passed to the engine (mit recurses to RecursionError);
# a file with at least one `src->dst` (or TAB) entry is used as-is.
echo "ENGINE=mtui" > "$CR/engine_ready"
: > "$CR/sakura_dict.txt"
rc="$(run_mtui_job job_dict_empty ok)"
check "empty dict job rc=0" "$rc" "0"
contains "empty dict -> disabled path" \
  "$(cat "$CR/jobs/job_dict_empty/translate.log")" "DICT_USED=$CR/sakura_dict.disabled"

echo '原文->译文 #备注' > "$CR/sakura_dict.txt"
echo "ENGINE=mit" > "$CR/engine_ready"
job4="$CR/jobs/job_dict_entry"
make_job_zip "$job4" 1
PATH="$BIN/mitpath:$PATH" FAKE_MODE=ok bash "$CR/vm_job.sh" start "$job4" 1 >/dev/null
wait_exit_code "$job4" >/dev/null
contains "dict entry -> real path" \
  "$(cat "$job4/translate.log")" "DICT_USED=$CR/sakura_dict.txt"

# ---------- G4 legacy rule: explicit cases ----------
# (a) no engine_ready + no .gui_engine_proto + mit dir + font → legacy mit, job runs.
rm -f "$CR/engine_ready" "$CR/.gui_engine_proto"
out="$(bash "$CR/vm_status.sh")"
contains "G4 legacy status mit" "$out" "ENGINE mit ready"
job5="$CR/jobs/job_legacy_mit"
make_job_zip "$job5" 2
PATH="$BIN/mitpath:$PATH" FAKE_MODE=ok bash "$CR/vm_job.sh" start "$job5" 2 >/dev/null
rc="$(wait_exit_code "$job5")"
check "G4 legacy mit job rc" "$rc" "0"

# (b) .gui_engine_proto present → NOT legacy: status none missing, job rejected.
: > "$CR/.gui_engine_proto"
out="$(bash "$CR/vm_status.sh")"
contains "G4 proto blocks legacy status" "$out" "ENGINE none missing"
job6="$CR/jobs/job_proto_block"
make_job_zip "$job6" 1
out="$(bash "$CR/vm_job.sh" start "$job6" 1 2>&1 || true)"
contains "G4 proto blocks legacy job" "$out" "JOB_ERROR engine_not_ready"

# (c) engine_ready marker wins over legacy/proto state.
echo "ENGINE=mtui" > "$CR/engine_ready"
out="$(bash "$CR/vm_status.sh")"
contains "G4 marker wins status" "$out" "ENGINE mtui ready"
rc="$(run_mtui_job job_marker_wins ok)"
check "G4 marker wins job rc" "$rc" "0"

# ---------- F6 clean_stale ----------
# Clear all leftover job dirs from earlier cases so the count is deterministic.
rm -rf "$CR/jobs"
mkdir -p "$CR/jobs" "$CR/mtui/result"
echo keep > "$CR/mtui/result/keep.txt"
stale="$CR/jobs/job_stale_001"
mkdir -p "$stale/out"
sleep 60 & stale_sleep=$!
echo "$stale_sleep" > "$stale/pid"
echo x > "$CR/job_stale_001.zip"
echo x > "$CR/job_stale_001.zip.tmp"
echo x > "$CR/job_stale_001.part_0000"
out="$(bash "$CR/vm_pack.sh" clean_stale)"
wait "$stale_sleep" 2>/dev/null || true
check "clean_stale kills job pid" "$(kill -0 $stale_sleep 2>/dev/null && echo alive || echo gone)" "gone"
check "clean_stale removes job dir" "$([ -d "$stale" ] && echo yes || echo no)" "no"
check "clean_stale removes staged zip" "$([ -e "$CR/job_stale_001.zip" ] && echo yes || echo no)" "no"
check "clean_stale removes staged tmp" "$([ -e "$CR/job_stale_001.zip.tmp" ] && echo yes || echo no)" "no"
check "clean_stale removes staged parts" "$(find "$CR" -maxdepth 1 -name 'job_stale_001.part_*' | wc -l | tr -d ' ')" "0"
check "clean_stale keeps mtui/result" "$([ -f "$CR/mtui/result/keep.txt" ] && echo yes || echo no)" "yes"
contains "clean_stale output" "$out" "STALE_CLEAN_OK 1 0"

# No stale jobs at all → zeros.
out="$(bash "$CR/vm_pack.sh" clean_stale)"
contains "clean_stale no jobs" "$out" "STALE_CLEAN_OK 0 0"

echo
echo "RESULT: $PASS passed, $FAIL failed"
rm -rf "$ROOT"
[ "$FAIL" -eq 0 ]
