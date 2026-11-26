#!/usr/bin/env python3
"""T1: mtui_patch.py / mtui_config.json / prompt_hq.yaml / sakura_dict.txt asset tests.

Usage: python3 test_mtui_assets.py [mtui_checkout_root]
Default checkout root: %TEMP%/ocr_audit/mtui (pinned commit 8cd6feec...).
Runs offline; exit code 1 when any check fails.
"""
import glob
import io
import json
import os
import py_compile
import re
import shutil
import subprocess
import sys
import tempfile

GUI_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ASSETS = os.path.join(GUI_ROOT, 'assets')
PATCH = os.path.join(ASSETS, 'mtui_patch.py')
CONFIG = os.path.join(ASSETS, 'mtui_config.json')
PROMPT_HQ = os.path.join(ASSETS, 'prompt_hq.yaml')
DICT = os.path.join(ASSETS, 'sakura_dict.txt')
DEFAULT_CHECKOUT = os.path.join(tempfile.gettempdir(), 'ocr_audit', 'mtui')

KANA_PATH = 'manga_translator/translators/sakura.py'
FILTER_PATH = 'manga_translator/manga_translator.py'
HQ_PATH = 'manga_translator/translators/openai_hq.py'

failures = []


def check(name, ok, detail=''):
    print(('PASS' if ok else 'FAIL'), name, detail)
    if not ok:
        failures.append(name)


def run_patch(root):
    return subprocess.run(
        [sys.executable, PATCH, root], capture_output=True, text=True, encoding='utf-8')


def main():
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    except (AttributeError, OSError):
        pass
    checkout = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_CHECKOUT
    if not os.path.isdir(checkout):
        print('FAIL checkout missing:', checkout)
        return 1

    with tempfile.TemporaryDirectory(prefix='mtui_patch_test_') as tmp:
        root = os.path.join(tmp, 'mtui')
        for rel in (KANA_PATH, FILTER_PATH, HQ_PATH):
            tgt = os.path.join(root, rel)
            os.makedirs(os.path.dirname(tgt), exist_ok=True)
            shutil.copy2(os.path.join(checkout, rel), tgt)

        # 1. Fresh run: all three files PATCHED, patched sources still compile.
        r = run_patch(root)
        check('patch first run exit 0', r.returncode == 0, r.stdout + r.stderr)
        patched_lines = [ln for ln in r.stdout.splitlines() if ln.startswith('PATCHED ')]
        check('PATCHED all three files', len(patched_lines) == 3, r.stdout)

        kana_src = io.open(os.path.join(root, KANA_PATH), encoding='utf-8').read()
        check('enlarge_small_kana call replaced',
              'queries = [self.enlarge_small_kana(query) for query in queries]' not in kana_src
              and 'enlarge_small_kana disabled' in kana_src)
        filt_src = io.open(os.path.join(root, FILTER_PATH), encoding='utf-8').read()
        check('MANGA_JA_ONLY filter inserted',
              "MANGA_JA_ONLY') == '1'" in filt_src
              and '[\\u3040-\\u30ff\\u3400-\\u9fff]' in filt_src)
        hq_src = io.open(os.path.join(root, HQ_PATH), encoding='utf-8').read()
        check('FALLBACK_SAKURA_API_BASE fallback inserted',
              '[FALLBACK_SAKURA]' in hq_src
              and "FALLBACK_SAKURA_API_BASE" in hq_src)
        for rel in (KANA_PATH, FILTER_PATH, HQ_PATH):
            try:
                py_compile.compile(os.path.join(root, rel), doraise=True)
                check(f'py_compile {rel}', True)
            except py_compile.PyCompileError as e:
                check(f'py_compile {rel}', False, str(e))

        # 2. Idempotent rerun: ALREADY_PATCHED for all three, exit 0.
        r2 = run_patch(root)
        check('rerun exit 0', r2.returncode == 0, r2.stdout + r2.stderr)
        check('rerun ALREADY_PATCHED x3',
              r2.stdout.count('ALREADY_PATCHED') == 3, r2.stdout)

        # 3. Corrupted file: marker absent AND original line count != 1 -> PATCH_FAIL, exit 1.
        kana = os.path.join(root, KANA_PATH)
        src = io.open(kana, encoding='utf-8').read()
        io.open(kana, 'w', encoding='utf-8', newline='').write(
            src.replace('enlarge_small_kana disabled', 'marker_removed'))
        r3 = run_patch(root)
        check('corrupt run exit 1', r3.returncode == 1, r3.stdout + r3.stderr)
        check('PATCH_FAIL emitted', 'PATCH_FAIL' in r3.stdout, r3.stdout)

    # 4. Non-Japanese filter regex: [\u3040-\u30ff\u3400-\u9fff] keeps kana/kanji lines.
    pat = re.compile('[\\u3040-\\u30ff\\u3400-\\u9fff]')
    for keep in ('はぁ', '．．．ッ', '丈', '聖女様♥'):
        check(f'keeps {keep}', pat.search(keep) is not None)
    for drop in ('Ｍａｃｒｉｎｄｏ', 'jmcgmic', '８０'):
        check(f'drops {drop}', pat.search(drop) is None)

    # 5. mtui_config.json: valid JSON, D6 values, every key exists in the pinned schema.
    cfg = json.load(io.open(CONFIG, encoding='utf-8'))
    check('config is dict', isinstance(cfg, dict))
    section_checks = [
        ('cli', 'verbose', False), ('cli', 'batch_size', 1),
        ('translator', 'translator', 'sakura'), ('translator', 'target_lang', 'CHS'),
        ('translator', 'remove_trailing_period', True),
        ('ocr', 'ocr', 'mocr'),
        ('detector', 'detector', 'default'), ('detector', 'use_yolo_obb', True),
        ('detector', 'use_sfx_filter', True),
        ('inpainter', 'inpainter', 'lama_large'),
        ('inpainter', 'inpainting_precision', 'bf16'),
        ('render', 'layout_mode', 'balloon_fill'), ('render', 'center_text_in_bubble', True),
        ('render', 'semantic_linebreak', True), ('render', 'font_family', 'Microsoft YaHei'),
        ('render', 'stroke_width', 0.15), ('render', 'font_scale_ratio', 1.15),
        ('upscale', 'upscale_ratio', None),
    ]
    for sect, key, want in section_checks:
        got = cfg.get(sect, {}).get(key)
        check(f'config {sect}.{key} == {want!r}', got == want, f'got {got!r}')
    check('config use_custom_api_params == False', cfg.get('use_custom_api_params') is False,
          repr(cfg.get('use_custom_api_params')))

    schema = ''
    for f in glob.glob(os.path.join(checkout, 'desktop_qt_ui', '**', '*.py'), recursive=True):
        schema += io.open(f, encoding='utf-8').read()
    cfg_py = os.path.join(checkout, 'manga_translator', 'config.py')
    if os.path.isfile(cfg_py):
        schema += io.open(cfg_py, encoding='utf-8').read()

    keys = []

    def walk(o):
        if isinstance(o, dict):
            for k, v in o.items():
                keys.append(k)
                walk(v)
    walk(cfg)
    missing = [k for k in keys
               if not re.search(r'(?m)^\s*' + re.escape(k) + r'\s*[:=]', schema)]
    check('all config keys exist in pinned schema', not missing, str(missing))

    # 6. prompt_hq.yaml exists with system_prompt & glossary sections.
    prompt_ok = False
    if os.path.isfile(PROMPT_HQ):
        prompt_text = io.open(PROMPT_HQ, encoding='utf-8').read()
        prompt_ok = 'system_prompt:' in prompt_text and 'glossary:' in prompt_text
    check('prompt_hq.yaml valid template', prompt_ok)

    # 7. sakura_dict.txt ships empty (user override lives in the scripts dir).
    check('sakura_dict.txt empty', os.path.isfile(DICT) and os.path.getsize(DICT) == 0)

    print()
    print(f'{len(failures)} failure(s)' if failures else 'ALL TESTS PASSED')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
