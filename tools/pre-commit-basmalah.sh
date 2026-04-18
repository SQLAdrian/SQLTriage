#!/usr/bin/env bash
# In the name of God, the Merciful, the Compassionate
# Pre-commit hook: enforce basmalah header on source files.
# Install: ln -sf ../../tools/pre-commit-basmalah.sh .git/hooks/pre-commit
# Or call from a wrapper .git/hooks/pre-commit script.

set -e

missing=0
changed=$(git diff --cached --name-only --diff-filter=ACM | grep -E '\.(cs|razor|css|js|md)$' || true)

[ -z "$changed" ] && exit 0

while IFS= read -r f; do
  [ -z "$f" ] && continue
  # Basmalah must appear in first 3 lines (English or Arabic).
  if ! head -n 3 "$f" | grep -qE "In the name of God, the Merciful, the Compassionate|بسم الله"; then
    echo "MISSING BASMALAH: $f"
    missing=1
  fi
done <<< "$changed"

if [ $missing -ne 0 ]; then
  echo ""
  echo "Commit blocked. Add basmalah header to the files above."
  echo "  .cs/.js/.css: /* In the name of God, the Merciful, the Compassionate */"
  echo "  .razor:       <!--/* In the name of God, the Merciful, the Compassionate */-->"
  echo "  .md:          <!-- In the name of God, the Merciful, the Compassionate -->"
  exit 1
fi

exit 0
