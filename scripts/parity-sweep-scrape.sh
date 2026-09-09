#!/usr/bin/env bash
# Differential sweep for `scrape` (and the worker protocol underneath it),
# the sibling of parity-sweep.sh, which covers `fetch`.
#
# `scrape` fans out to one obscura-worker process per URL over a
# newline-delimited JSON protocol, so this exercises three things at once: the
# CLI's flag handling and output format, the parent/worker wire protocol, and
# the worker's own navigate/evaluate/shutdown handling. Only running both
# engines finds a misreading of the original; the ported unit tests inherit it.
#
# Wall-clock fields cannot match, so they are normalized: time_ms,
# total_time_ms and avg_time_ms in the JSON output, and the leading "<n>ms" in
# the text output. Everything else is compared byte for byte, including key
# order, number formatting (serde_json prints an f64 with a decimal point), and
# the stderr progress lines.
#
# Usage: scripts/parity-sweep-scrape.sh [rust-binary] [csharp-binary]
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
  worker="$(dirname "$bin")/obscura-worker"
  if [[ ! -x "$worker" ]]; then
    echo "worker binary missing next to $bin: $worker" >&2
    exit 1
  fi
done

# The fixtures are local files, so no network and no per-run variation.
export OBSCURA_ALLOW_PRIVATE_NETWORK=1
# anyhow appends a backtrace to a top-level error when this is set, which the
# port has no equivalent for and which would report every error case as a
# difference.
unset RUST_BACKTRACE RUST_LIB_BACKTRACE

# Blank the fields that are wall-clock or process-identity dependent.
normalize() {
  sed -E \
    -e 's/"(total_)?time_ms": [0-9]+/"\1time_ms": <MS>/' \
    -e 's/"avg_time_ms": [0-9]+(\.[0-9]+)?/"avg_time_ms": <MS>/' \
    -e 's/^[0-9]+ms\t/<MS>\t/' \
    -e 's/^Total: [0-9]+ms /Total: <MS>ms /'
}

pass=0
fail=0
failed=()

# One case: run both engines with the same argv, diff normalized stdout+stderr.
compare() {
  local label="$1"
  shift
  local r c
  r="$("$RUST" "$@" 2>&1 | normalize)"
  c="$("$CS" "$@" 2>&1 | normalize)"
  if [[ "$r" == "$c" ]]; then
    pass=$((pass + 1))
  else
    fail=$((fail + 1))
    failed+=("$label")
    if [[ -n "${PARITY_VERBOSE:-}" ]]; then
      diff <(printf '%s\n' "$r") <(printf '%s\n' "$c") | sed 's/^/    /'
    fi
  fi
}

fixtures=()
while IFS= read -r f; do fixtures+=("file://$f"); done < <(ls "$REPO"/render-repros/*.html)

# 1. Every fixture on its own, default (json) format.
for url in "${fixtures[@]}"; do
  compare "scrape $(basename "$url") --quiet" scrape "$url" --quiet
done

# 2. Every fixture on its own, text format.
for url in "${fixtures[@]}"; do
  compare "scrape $(basename "$url") --format text --quiet" \
    scrape "$url" --format text --quiet
done

# 3. --eval, whose value crosses the worker protocol and is re-serialized by
#    the parent. Covers the number/string/object/array/null shapes separately,
#    because serde_json formats an f64 differently from a parsed integer.
evals=(
  '1+1'
  '"hi"'
  'document.title'
  '({a:1,b:[2,3],c:null})'
  '[1,2,3]'
  'null'
  'undefined'
  'true'
  'document.querySelectorAll("*").length'
  '(async()=>{await new Promise(r=>setTimeout(r,0));return "async-done"})()'
  'throw new Error("boom")'
)
sample="${fixtures[0]}"
for expr in "${evals[@]}"; do
  compare "scrape --eval $expr" scrape "$sample" --quiet --eval "$expr"
  compare "scrape --format text --eval $expr" \
    scrape "$sample" --quiet --format text --eval "$expr"
done

# 4. Batches at several concurrencies: result order must follow argv order, not
#    completion order, and total_urls/concurrency must be echoed as given.
batch=("${fixtures[@]:0:6}")
for c in 1 2 3 10; do
  compare "scrape 6 urls --concurrency $c" \
    scrape "${batch[@]}" --concurrency "$c" --quiet
  compare "scrape 6 urls --concurrency $c --format text" \
    scrape "${batch[@]}" --concurrency "$c" --quiet --format text
done

# 5. Progress lines on stderr, i.e. without --quiet.
compare "scrape not quiet" scrape "$sample"
compare "scrape not quiet --format text" scrape "$sample" --format text

# 6. Per-URL failures must be reported in-band, with the run still exiting 0.
compare "scrape unreachable url" \
  scrape "file:///definitely/not/here.html" --quiet
compare "scrape mixed ok and failing" \
  scrape "$sample" "file:///definitely/not/here.html" --quiet
compare "scrape unparseable url" scrape "not-a-url" --quiet

# 7. Usage and validation errors: the message, the stream, and the exit code.
compare_status() {
  local label="$1"
  shift
  local r c rs cs
  r="$("$RUST" "$@" 2>&1 | normalize)"; rs=$?
  c="$("$CS" "$@" 2>&1 | normalize)"; cs=$?
  if [[ "$r" == "$c" && "$rs" == "$cs" ]]; then
    pass=$((pass + 1))
  else
    fail=$((fail + 1))
    failed+=("$label (exit $rs vs $cs)")
    if [[ -n "${PARITY_VERBOSE:-}" ]]; then
      diff <(printf '%s\n' "$r") <(printf '%s\n' "$c") | sed 's/^/    /'
    fi
  fi
}

compare_status "scrape with no urls" scrape
compare_status "scrape --timeout 0" scrape "$sample" --timeout 0 --quiet
compare_status "scrape --concurrency 0" scrape "$sample" --concurrency 0 --quiet

# 8. The banner and the bare-server path's own output. `serve` blocks forever,
#    so only the part before the listen loop is comparable: run it against a
#    port that is already taken and diff what each engine printed first.
banner() {
  local bin="$1" port="$2"
  timeout 5 "$bin" --port "$port" 2>/dev/null | sed -E 's/(Headless Browser v).*/\1<VER>/'
}
r="$(banner "$RUST" 19801)"
c="$(banner "$CS" 19802)"
if [[ "$r" == "${c//19802/19801}" ]]; then
  pass=$((pass + 1))
else
  fail=$((fail + 1))
  failed+=("bare server banner")
  [[ -n "${PARITY_VERBOSE:-}" ]] &&
    diff <(printf '%s\n' "$r") <(printf '%s\n' "${c//19802/19801}") | sed 's/^/    /'
fi

serve_banner() {
  local bin="$1" port="$2"
  timeout 5 "$bin" serve --port "$port" 2>/dev/null |
    sed -E 's/(Headless Browser v).*/\1<VER>/'
}
r="$(serve_banner "$RUST" 19803)"
c="$(serve_banner "$CS" 19804)"
if [[ "$r" == "${c//19804/19803}" ]]; then
  pass=$((pass + 1))
else
  fail=$((fail + 1))
  failed+=("serve banner")
  [[ -n "${PARITY_VERBOSE:-}" ]] &&
    diff <(printf '%s\n' "$r") <(printf '%s\n' "${c//19804/19803}") | sed 's/^/    /'
fi

# 9. The worker binary directly, which is the protocol contract `scrape`
#    depends on. Same command script into both, diff the reply lines.
worker_script() {
  printf '%s\n' \
    '{"cmd":"navigate","url":"'"$sample"'"}' \
    '{"cmd":"title"}' \
    '{"cmd":"evaluate","expression":"1+1"}' \
    '{"cmd":"evaluate","expression":"document.title"}' \
    '{"cmd":"dump_text"}' \
    '{"cmd":"dump_html"}' \
    '' \
    '{"cmd":"shutdown"}'
}
r="$(worker_script | timeout 60 "$(dirname "$RUST")/obscura-worker" 2>/dev/null)"
c="$(worker_script | timeout 60 "$(dirname "$CS")/obscura-worker" 2>/dev/null)"
if [[ "$r" == "$c" ]]; then
  pass=$((pass + 1))
else
  fail=$((fail + 1))
  failed+=("worker protocol")
  [[ -n "${PARITY_VERBOSE:-}" ]] &&
    diff <(printf '%s\n' "$r") <(printf '%s\n' "$c") | sed 's/^/    /'
fi

printf '%d identical, %d differ\n' "$pass" "$fail"
if [[ ${#failed[@]} -gt 0 ]]; then
  for case in "${failed[@]}"; do
    printf '  differs: %s\n' "$case"
  done
  echo "re-run with PARITY_VERBOSE=1 for the diffs" >&2
fi

[[ $fail -eq 0 ]]
