---
name: obscura-port
description: Port a component of the Obscura headless browser engine from the Rust reference implementation in crates/ to the C# / .NET 10 implementation in dotnet/. Use when porting or reviewing ported code for the DOM tree, selectors, HTML parsing, the V8 op layer, CSS parsing and cascade, layout, paint, the Page API, the CDP server, the MCP server, or the CLI, when writing xUnit ports of Rust tests, or when validating C# output against the Rust binary.
---

# Porting Obscura to C#

The Rust workspace in `crates/` is the specification. The C# tree in `dotnet/`
is the port. Read `CLAUDE.md` for the ground rules and `todo.md` for the queue
and the list of accepted deviations before starting.

## The order that works

Port a component in this order. Skipping a step produces code that compiles and
is wrong in ways tests do not catch.

1. **Read the whole Rust file first.** Do not port function by function while
   reading. The Rust code carries invariants in comments that only make sense
   with the whole file in view, and several of them are load-bearing (the DOM
   reparenting guards, the `descendants()` cap, the op panic-safety wrapper).
2. **Port the types and public surface**, then the bodies. Getting the shape
   right first keeps the call sites honest.
3. **Port the in-file `#[cfg(test)] mod tests` as xUnit facts, in the same
   order and with the same names.** These encode the edge cases someone already
   found the hard way.
4. **Port the integration tests** under `crates/<crate>/tests/` to
   `dotnet/tests/Obscura.<Area>.Tests/`, keeping the file name.
5. **Run `dotnet test` for the area.** Green means plausible, not done.
6. **Add a parity case** that runs the same input through both engines. Green
   parity means done.
7. **Update `todo.md`**: flip the line, and record any deliberate difference
   under Known deviations with a reason.

## Translation notes

These are the mappings that come up constantly.

| Rust | C# |
|---|---|
| `Option<T>` | `T?` (annotate reference types; do not use a sentinel) |
| `Result<T, E>` | throw for programmer error; return a status/`bool` + `out` where the Rust code branches on the error |
| `&str` / `String` | `string`; use `ReadOnlySpan<char>` on hot paths |
| `Vec<T>` | `List<T>`, or `T[]` when the length is fixed at construction |
| `HashMap` / `HashSet` | `Dictionary` / `HashSet`, with an explicit `StringComparer.Ordinal` |
| `RefCell<T>` / interior mutability | a plain mutable field; the engine is single-threaded per page |
| `Rc<RefCell<T>>` | a class reference |
| `enum` with data | a sealed record hierarchy, or a struct + discriminant on hot paths |
| `match` | `switch` expression with pattern matching |
| `impl Trait` / trait objects | an interface, or a delegate for single-method traits |
| `#[derive(Clone, Copy)]` small structs | `readonly record struct` |

**Ordinal string comparison is not optional.** Rust `==` on `&str` is ordinal.
A C# default comparison is culture-sensitive and will diverge on Turkish `i`,
among others. `InvariantGlobalization` is on, which helps, but be explicit.

**Number formatting is observable.** Rust's `{}` for `f64` and .NET's `ToString()`
disagree on some values. Anything that crosses the op boundary or lands in JSON
must format the way the Rust code formats it; check against a parity test.

## Things that will bite you

- **Never let an exception reach V8.** Route op bodies through `OpGuard`. An
  exception crossing the FFI boundary is a process abort in Rust and undefined
  behavior here; both are worse than the documented failure value.
- **`op_dom` returns strings, including for failure.** A missing node is `""` or
  `"null"` depending on the command. Copy `op_dom_inner`'s choice exactly.
- **The DOM reparenting guards are load-bearing.** `AppendChild`/`InsertBefore`
  reject cycles. Removing the guard makes `Descendants()` loop forever on real
  sites, uninterruptibly.
- **`insertBefore`/`replaceChild` argument order** in the shim is easy to get
  backwards. Verify `before()`, `after()`, `replaceWith()`, and `replaceChild()`
  on connected elements after touching mutation ops.
- **`canAccessOpener` must appear in every `TargetInfo`** or strict CDP clients
  panic.
- **SkiaSharp 4.x paths are immutable.** Build with `SKPathBuilder` + `Detach()`.
- **Never resolve typefaces by family name.** The engine ships its own fonts and
  runs where no fontconfig exists. Use `SKTypeface.FromData` over the embedded
  resources.

## Validating against the Rust engine

Build the reference binary once:

```bash
CARGO_INCREMENTAL=0 CARGO_BUILD_JOBS=2 cargo build --release -p obscura-cli --bins --features render
export OBSCURA_RUST_BIN="$PWD/target/release/obscura"
```

`dotnet/tests/Obscura.Parity.Tests` skips itself when `OBSCURA_RUST_BIN` is
unset, so parity coverage silently disappears if you forget to export it. Check
that the parity tests actually ran before claiming a component is done.

Compare the cheap, high-signal surfaces first: `--dump text`, `--dump links`,
`--dump html`, and `--eval` results over the fixtures in `render-repros/`. Those
catch structural divergence long before pixels do. For rendering, treat a pixel
metric as a regression tripwire and not a verdict, exactly as the Rust project
does: confirm both engines navigated and produced nonblank output, then inspect
resource completion, box geometry, line wrapping, and clipping.

## Reporting

Say what is ported, what is tested, and what is validated against Rust, as three
separate claims. "Ported" without parity evidence is not done, and describing it
as done is the failure mode this skill exists to prevent.
