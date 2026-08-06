#!/usr/bin/env bash
# Regression test: formula renders and is valid Ruby (no network).
# Run on ubuntu only.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FIX="${ROOT}/scripts/fixtures/homebrew-ol-checksums.txt"
RENDER="${ROOT}/scripts/render-homebrew-ol-formula.sh"
OUT="$(mktemp)"
trap 'rm -f "$OUT"' EXIT

bash "$RENDER" "1.2.3" "v1.2.3" "acme/ol" "$FIX" > "$OUT"

grep -q 'aaa1111111111111111111111111111111111111111111111111111111111111' "$OUT"
grep -q 'ol-osx-arm64.tar.gz' "$OUT"
grep -q 'https://github.com/acme/ol/releases/download/v1.2.3/' "$OUT"
grep -q 'system "#{bin}/ol", "--version"' "$OUT"

if command -v ruby >/dev/null 2>&1; then
  ruby -c "$OUT" >/dev/null
else
  echo "test-homebrew-formula-render: ruby not found; skipping ruby -c" >&2
fi

echo "test-homebrew-formula-render: OK"
