#!/usr/bin/env python3
# .claude/hooks/validate-no-shorthand.py
# PreToolUse: Blocks Write/Edit/Bash calls containing uncompressed shorthands.

import json
import re
import sys
from pathlib import Path


def load_vocab(vocab_path):
    active = set()
    if not vocab_path.exists():
        return active
    for line in vocab_path.read_text(encoding="utf-8").splitlines():
        if line.startswith("|"):
            parts = [p.strip() for p in line.split("|")]
            if (
                len(parts) >= 5
                and parts[1] not in ("Shorthand", "")
                and parts[4] == "active"
            ):
                active.add(parts[1])
    return active


def find_shorthands(text, vocab):
    found = []
    for token in vocab:
        pattern = r"\b" + re.escape(token) + r"\b"
        if re.search(pattern, text):
            found.append(token)
    return found


def main():
    try:
        data = json.loads(sys.stdin.read())
    except Exception:
        sys.exit(0)

    tool_name = data.get("tool_name", "")
    tool_input = data.get("tool_input", {})

    if tool_name not in ("Write", "Edit", "NotebookEdit", "Bash"):
        sys.exit(0)

    content = ""
    if tool_name in ("Write", "Edit"):
        content = tool_input.get("content", "") or ""
    elif tool_name == "Bash":
        content = tool_input.get("command", "") or ""
    elif tool_name == "NotebookEdit":
        content = str(tool_input.get("cells", ""))

    script_dir = Path(__file__).parent
    project_root = script_dir.parent.parent
    vocab = load_vocab(project_root / "memory" / "session_vocab.md")

    violations = find_shorthands(content, vocab)

    if violations:
        print(
            f"ERROR: shorthand token(s) in {tool_name} input: {', '.join(violations)}",
            file=sys.stderr,
        )
        print(
            f"All tokens must be expanded before writing files or running commands.",
            file=sys.stderr,
        )
        sys.exit(2)

    sys.exit(0)


if __name__ == "__main__":
    main()
