#!/usr/bin/env bash
# Regenerate the parity golden corpus from the Rust reference engine.
#
# The goldens let the C# port be compared against recorded Rust behavior on a
# machine with no Rust toolchain. They are a convenience, not the authority:
# the live differential tests in Obscura.Parity.Tests run both engines and are
# what actually gates a component. Regenerate whenever the Rust engine changes.
#
# Usage: scripts/regen-golden.sh [path-to-obscura-binary]
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BIN="${1:-${OBSCURA_RUST_BIN:-$REPO/target/release/obscura}}"
OUT="$REPO/dotnet/tests/Obscura.Parity.Tests/golden"

if [[ ! -x "$BIN" ]]; then
  echo "reference binary not found: $BIN" >&2
  echo "build it with: cargo build --release -p obscura-cli --bins --features render" >&2
  exit 1
fi

mkdir -p "$OUT"
rm -f "$OUT"/*.txt

count=0
for fixture in "$REPO"/render-repros/*.html; do
  name="$(basename "$fixture" .html)"
  for mode in text links html; do
    if out="$("$BIN" fetch "file://$fixture" --dump "$mode" --quiet 2>/dev/null)"; then
      printf '%s' "$out" > "$OUT/$name.$mode.txt"
      count=$((count + 1))
    else
      echo "  skipped (nonzero exit): $name --dump $mode" >&2
    fi
  done
done

printf 'wrote %d golden files to %s\n' "$count" "$OUT"
printf 'reference: %s (%s)\n' "$BIN" "$("$BIN" --version)"
