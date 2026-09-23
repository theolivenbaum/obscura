# .reference

Upstream source this repository was ported from. Read-only.

| Upstream | Vendored at | Synced to |
|---|---|---|
| [Obscura](https://github.com/h4ckf0r0day/obscura) (Rust) | `obscura/` | see `obscura/UPSTREAM.md` |

The tree is upstream's, with its own README, LICENSE, docs, disabled CI
workflows and vendored crates (`vendor/taffy`, `vendor/cosmic-text`), so it can
be diffed against upstream. `obscura/UPSTREAM.md` names the upstream commit it
was taken from and lists the few edits the fork made to it early in the port.
Nothing in `dotnet/` builds from it or reads it; it is the specification the
port was written against, and the baseline for merging newer upstream work.

`obscura/render-repros` is a symlink to the repository's own `render-repros/`: the Rust render tests
`include_str!` fixtures from there, and the fixtures live at the root because
the C# parity suite drives them too.

See "Merging upstream changes" in the root `CLAUDE.md` for how this tree is
updated.
