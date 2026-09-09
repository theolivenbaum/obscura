#!/usr/bin/env bash
# Differential sweep: run every render-repros fixture through both engines and
# diff the output byte for byte.
#
# This is the check that actually validates the port. The unit tests compare the
# port against tests that were themselves ported, so they inherit any misreading
# of the original; only running both engines finds that class of bug. It has
# already caught several, including two selector bugs that had green unit tests
# sitting on top of them.
#
# Usage: scripts/parity-sweep.sh [rust-binary] [csharp-binary]
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUST="${1:-${OBSCURA_RUST_BIN:-$REPO/target/release/obscura}}"
CS="${2:-${OBSCURA_PORT_BIN:-$REPO/dotnet/src/Obscura.Cli/bin/Release/net10.0/obscura}}"

for bin in "$RUST" "$CS"; do
  if [[ ! -x "$bin" ]]; then
    echo "not executable: $bin" >&2
    echo "build the reference with: cargo build --release -p obscura-cli --bins --features render" >&2
    echo "build the port with:      cd dotnet && dotnet build -c Release" >&2
    exit 1
  fi
done

pass=0
fail=0
failed=()

for fixture in "$REPO"/render-repros/*.html; do
  name="$(basename "$fixture" .html)"
  for mode in text links html markdown assets; do
    r="$("$RUST" fetch "file://$fixture" --dump "$mode" --quiet 2>/dev/null)"
    c="$("$CS" fetch "file://$fixture" --dump "$mode" --quiet 2>/dev/null)"
    if [[ "$r" == "$c" ]]; then
      pass=$((pass + 1))
    else
      fail=$((fail + 1))
      failed+=("$name --dump $mode")
    fi
  done
done

printf '%d identical, %d differ\n' "$pass" "$fail"
if [[ ${#failed[@]} -gt 0 ]]; then
  for case in "${failed[@]}"; do
    printf '  differs: %s\n' "$case"
  done
fi

[[ $fail -eq 0 ]]
