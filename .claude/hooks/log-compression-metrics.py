#!/usr/bin/env python3
# .claude/hooks/log-compression-metrics.py
# PostToolUse: append per-turn compression stats to memory/compression-metrics.jsonl

import json
import sys
from datetime import datetime, timezone
from pathlib import Path


def main():
    try:
        data = json.loads(sys.stdin.read())
    except Exception:
        sys.exit(0)

    compression = data.get("_compression")
    if not compression:
        sys.exit(0)

    script_dir = Path(__file__).parent
    project_root = script_dir.parent.parent
    metrics_path = project_root / "memory" / "compression-metrics.jsonl"

    entry = {
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "session_id": data.get("session_id", ""),
        "original_chars": compression["original_chars"],
        "compressed_chars": compression["compressed_chars"],
        "saved_chars": compression["saved_chars"],
        "saved_pct": compression["saved_pct"],
        "matches": compression["matches"],
    }

    with metrics_path.open("a", encoding="utf-8") as f:
        f.write(json.dumps(entry) + "\n")

    sys.exit(0)


if __name__ == "__main__":
    main()
