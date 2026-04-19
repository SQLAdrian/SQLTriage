#!/usr/bin/env python3
# .claude/hooks/route-dsl.py
# Minimal routing: detects ACT->SRC->ACT triage pattern only.
# No embeddings. No external deps. ~20 lines.

import json, re, sys
from pathlib import Path


def load_type_map(project_root):
    path = project_root / "memory" / "dsl_types.json"
    if not path.exists():
        return {}
    try:
        return {k: v.get("type") for k, v in json.loads(path.read_text()).items()}
    except Exception:
        return {}


def main():
    try:
        data = json.loads(sys.stdin.read())
    except Exception:
        sys.exit(0)

    prompt = data.get("prompt", "")
    script_dir = Path(__file__).parent
    project_root = script_dir.parent.parent

    type_map = load_type_map(project_root)
    if not type_map:
        sys.exit(0)

    tokens = re.findall(r"([A-Z]{2,6})\(\)", prompt)
    types = [type_map.get(t) for t in tokens]

    if None in types:
        unknown = [t for t, v in zip(tokens, types) if v is None]
        print(f"ERROR: unknown DSL token(s): {', '.join(unknown)}", file=sys.stderr)
        sys.exit(2)

    if types == ["ACT", "SRC", "ACT"]:
        print("[ROUTE] triage pipeline (collect → analyze → collect)", file=sys.stderr)

    sys.exit(0)


if __name__ == "__main__":
    main()
