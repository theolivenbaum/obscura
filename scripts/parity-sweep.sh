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
crash=0
failed=()
crashed=()

# An engine that dies mid-run reports no output, which compares as a parity
# difference and sends you looking for a divergence that is not there. That is
# how the port's teardown segfault stayed hidden: it exited 139 after writing
# correct output, and comparing stdout alone called it a match while comparing
# stdout after a lost flush called it a difference. Check exit status
# separately, and name a signal death (128+n, so 137 is SIGKILL and 139 is
# SIGSEGV) as what it is.
run() {
  local engine="$1" fixture="$2" mode="$3"
  OUT="$("$engine" fetch "file://$fixture" --dump "$mode" --quiet 2>/dev/null)"
  STATUS=$?
}

for fixture in "$REPO"/render-repros/*.html; do
  name="$(basename "$fixture" .html)"
  for mode in text links html markdown assets; do
    run "$RUST" "$fixture" "$mode"; r="$OUT"; rs=$STATUS
    run "$CS" "$fixture" "$mode"; c="$OUT"; cs=$STATUS

    if [[ $rs -ge 128 || $cs -ge 128 ]]; then
      crash=$((crash + 1))
      crashed+=("$name --dump $mode (rust exit $rs, port exit $cs)")
    elif [[ "$r" == "$c" ]]; then
      pass=$((pass + 1))
    else
      fail=$((fail + 1))
      failed+=("$name --dump $mode")
    fi
  done
done

printf '%d identical, %d differ' "$pass" "$fail"
if [[ $crash -gt 0 ]]; then
  printf ', %d could not be compared\n' "$crash"
else
  printf '\n'
fi
for case in "${failed[@]}"; do
  printf '  differs: %s\n' "$case"
done
for case in "${crashed[@]}"; do
  printf '  engine died: %s\n' "$case"
done
if [[ $crash -gt 0 ]]; then
  printf 'An engine died on a signal, so those cases prove nothing either way.\n' >&2
  printf 'Check the exit code before assuming a divergence: 137 is SIGKILL, which\n' >&2
  printf 'usually means the OOM killer, and 139 is SIGSEGV, which means a bug.\n' >&2
fi

[[ $fail -eq 0 && $crash -eq 0 ]]
