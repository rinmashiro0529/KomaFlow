#!/usr/bin/env python3
"""Patch manga-translator-ui @ 8cd6feec33a66ace50042f75676965eb3d4d8a10.

Usage: python3 mtui_patch.py <mtui_root>

Patches three files:
1. manga_translator/translators/sakura.py — drop the enlarge_small_kana
   preprocessing line (converting small kana like ッ to ツ made Sakura
   emit the stray character 兹).
2. manga_translator/manga_translator.py — in _filter_ocr_textlines, drop OCR
   lines that contain no kana or kanji when MANGA_JA_ONLY=1 (removes
   watermark gibberish; bubbles containing only …/！？/♥ keep the original
   text, which is an accepted side effect).
3. manga_translator/translators/openai_hq.py — at page retry exhaustion, use
   the optional local Sakura endpoint. Without that endpoint, re-raise as before.

Per file prints PATCHED / ALREADY_PATCHED / PATCH_FAIL.
Exit code 0 when every file is PATCHED or ALREADY_PATCHED, else 1.
Stdlib only; safe to run repeatedly (idempotent).
"""
import sys

KANA_PATH = 'manga_translator/translators/sakura.py'
KANA_OLD = '        queries = [self.enlarge_small_kana(query) for query in queries]\n'
KANA_NEW = '        # MangaGui patch: enlarge_small_kana disabled (ッ->ツ made Sakura emit stray 兹)\n'
KANA_MARKER = 'enlarge_small_kana disabled'

FILTER_PATH = 'manga_translator/manga_translator.py'
FILTER_MARKER = 'MANGA_JA_ONLY'
# Anchor: start of the per-textline loop in _filter_ocr_textlines (pinned commit).
# The filter is inserted right after the empty-text skip. The target file already
# provides `os`, `re` (regex module) and `logger` at module scope.
FILTER_OLD = (
    '        low_confidence_count = 0\n'
    '\n'
    '        for textline in textlines:\n'
    '            text = self._get_textline_text(textline)\n'
    '            if not text.strip():\n'
    '                continue\n'
)
FILTER_NEW = FILTER_OLD + (
    "            if os.environ.get('MANGA_JA_ONLY') == '1' and not re.search('[\\u3040-\\u30ff\\u3400-\\u9fff]', text):\n"
    "                logger.info(f'OCR filtered non-Japanese text line: \"{text}\"')\n"
    '                continue\n'
)


HQ_PATH = 'manga_translator/translators/openai_hq.py'
HQ_END = '        raise last_exception if last_exception else Exception("OpenAI translation failed after all retries")'
HQ_FALLBACK = '''        fallback = os.environ.get('FALLBACK_SAKURA_API_BASE')
        if fallback:
            import json
            from urllib.request import Request, urlopen

            def translate_line(text):
                payload = {'model': 'sakura-galtransl-14b', 'messages': [
                    {'role': 'system', 'content': '你是日语漫画翻译。将日文译成自然的简体中文，只输出译文。'},
                    {'role': 'user', 'content': text}], 'temperature': 0.1}
                request = Request(fallback.rstrip('/') + '/chat/completions',
                                  data=json.dumps(payload).encode('utf-8'),
                                  headers={'Content-Type': 'application/json'})
                with urlopen(request, timeout=120) as response:
                    choice = json.load(response)['choices'][0]
                result = choice['message']['content'].strip()
                if not result or choice.get('finish_reason') != 'stop':
                    raise RuntimeError('Incomplete Sakura fallback response')
                return result

            self.logger.warning('[FALLBACK_SAKURA] Primary retries exhausted; translating %d lines', len(texts))
            translations = []
            for text in texts:
                self._check_cancelled()
                translations.append(await asyncio.to_thread(translate_line, text))
            return translations[:len(texts)]
'''
# The pinned source raises in two except branches *inside* the retry loop, so
# adding a handler just after the loop would miss normal page failures.
HQ_EDITS = []
for indent, anchor in [
    (16, 'raise Exception(f"Maximum attempts reached ({self._max_total_attempts}). Last error: {last_error_msg}")'),
    (24, 'raise last_exception'),
    (20, 'raise last_exception'),
]:
    old = '\n' + ' ' * indent + anchor + '\n'
    if indent == 20:
        # Match the generic except block, not the similar raise in the try body
        # (that one already flows into this handler).
        old = ('self.logger.warning(f"OpenAI high-quality translation failed ({log_attempt}): {e}")\n'
               '                \n                if not is_infinite and attempt >= max_retries:\n'
               '                    self.logger.error("OpenAI translation still failed after multiple retries. Terminating.")' + old)
    HQ_EDITS.append((old, old.replace(anchor, "if os.environ.get('FALLBACK_SAKURA_API_BASE'):\n" + ' ' * (indent + 4) + 'break\n' + ' ' * indent + anchor, 1)))
rotation = '            except APIRotationExhaustedError:\n                raise\n'
HQ_EDITS.append((rotation, rotation.replace('                raise\n',
                 "                if os.environ.get('FALLBACK_SAKURA_API_BASE'):\n                    break\n                raise\n")))
HQ_EDITS.append((HQ_END, HQ_FALLBACK + HQ_END))


def patch_file(path, edits, marker):
    try:
        with open(path, encoding='utf-8') as f:
            src = f.read()
    except OSError as exc:
        print('PATCH_FAIL', path, 'read_error=', exc)
        return False

    if marker in src:
        print('ALREADY_PATCHED', path)
        return True

    for before, after in edits:
        n = src.count(before)
        if n != 1:
            print('PATCH_FAIL', path, 'occurrences=', n)
            return False
        src = src.replace(before, after)

    try:
        with open(path, 'w', encoding='utf-8', newline='') as f:
            f.write(src)
    except OSError as exc:
        print('PATCH_FAIL', path, 'write_error=', exc)
        return False

    print('PATCHED', path)
    return True


def main():
    if len(sys.argv) != 2:
        print('usage: python3 mtui_patch.py <mtui_root>', file=sys.stderr)
        return 1
    root = sys.argv[1].rstrip('/\\')

    ok = patch_file(f'{root}/{KANA_PATH}', [(KANA_OLD, KANA_NEW)], KANA_MARKER)
    ok = patch_file(f'{root}/{FILTER_PATH}', [(FILTER_OLD, FILTER_NEW)], FILTER_MARKER) and ok
    ok = patch_file(f'{root}/{HQ_PATH}', HQ_EDITS, '[FALLBACK_SAKURA]') and ok
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
