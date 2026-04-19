#!/usr/bin/env python3
# .claude/hooks/compress-prompt.py
# Preprocessor: expand domain shorthands to full phrases before LLM sees prompt.
# Lossless: backtick spans preserved. No external deps. stdin → stdout JSON.

import json
import re
import sys
from pathlib import Path


def load_vocab(vocab_path):
    vocab = {}
    if not vocab_path.exists():
        return vocab
    for line in vocab_path.read_text(encoding="utf-8").splitlines():
        if line.startswith("|"):
            parts = [p.strip() for p in line.split("|")]
            if (
                len(parts) >= 5
                and parts[1] not in ("Shorthand", "")
                and parts[4] == "active"
            ):
                vocab[parts[1]] = parts[2]
    return vocab


def compress(prompt, vocab):
    parts = re.split(r"(`[^`]*`)", prompt)
    matches = []
    result = []
    for part in parts:
        if part.startswith("`") and part.endswith("`"):
            result.append(part)
        else:
            for shorthand, full in vocab.items():
                pattern = r"\b" + re.escape(shorthand) + r"\b"
                if re.search(pattern, part):
                    part, count = re.subn(pattern, full, part)
                    matches.extend([shorthand] * count)
            result.append(part)
    compressed = "".join(result)
    return compressed, matches


def main():
    try:
        data = json.loads(sys.stdin.read())
    except Exception:
        sys.exit(0)

    prompt = data.get("prompt", "")
    session_id = data.get("session_id", "")

    script_dir = Path(__file__).parent
    project_root = script_dir.parent.parent
    vocab = load_vocab(project_root / "memory" / "session_vocab.md")

    compressed, matches = compress(prompt, vocab)

    orig_len = len(prompt)
    comp_len = len(compressed)
    saved = orig_len - comp_len
    saved_pct = round((saved / orig_len * 100), 2) if orig_len else 0

    result = {
        "prompt": compressed,
        "session_id": session_id,
        "_compression": {
            "original_chars": orig_len,
            "compressed_chars": comp_len,
            "saved_chars": saved,
            "saved_pct": saved_pct,
            "matches": matches,
        },
    }
    print(json.dumps(result))
    sys.exit(0)


if __name__ == "__main__":
    main()
