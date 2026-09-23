# Upstream sync point

This tree is `https://github.com/h4ckf0r0day/obscura` at:

- Commit: `727cc46d56290995245fbe790caed52fc699452a`
- Date: 2026-09-06
- Subject: Merge pull request #859 from mrummuka/fix/docker-nonroot

moved from the repository root into `.reference/obscura/`, plus the edits the fork
made to it before the Rust tree became read-only. The C# port in `dotnet/` covers
upstream up to this commit.

## Where this tree differs from that commit

Build and CI were switched off when the port started (`e64d15d`):
`.github/workflows/*.yml`, `scripts/ci/*.py` and `Dockerfile` carry a `.disabled`
suffix, and `skills/obscura/SKILL.md` was adapted for the port. `build.log`,
`.gitignore`, `.gitattributes` and `tools/` stay at the repository root.

Early in the port some fixes were made to both engines at once, so the reference
binary kept parity with the C#. They touch `crates/obscura-js/js/bootstrap.js`,
`crates/obscura-js/src/runtime.rs`, and `crates/obscura-render/src/{dom,inline,lib,paint,style}.rs`
with `crates/obscura-render/tests/layout_test.rs`:

- `1837d51` Implement canvas 2D in the shared shim, replacing a one-operation stub
- `97cf9f8` render: give blurred box-shadow a gaussian falloff in both engines
- `d55c362` render: honor radial-gradient length stops and the shorthand's color layer
- `8ed5c49` render: paint inset box-shadow, and size buttons with the shaper
- `71a0d1d` render: implement filter: blur() in both engines
- `c974d15` render: implement backdrop-filter: blur() in both engines
- `af1e63a` render: draw rounded corners as arcs, not parabolas
- `86740c6` Resolve calc() percentages against a flex item's used width
- `8747a6e` Fix six bootstrap.js defects found driving a real SPA over CDP
- `761d732` innerText: tab-separate table cells like Chromium
- `172014d` Report the subresources the host fetched, and answer fonts.check() from the renderer
- `7ef4f00` Keep the document on a fragment navigation, and fire the right events

No further edits are made here (CLAUDE.md, rule 1). Because of these, an upstream
diff does not apply cleanly to every file; the sync procedure in the root
`CLAUDE.md` applies it three-way, and conflicts land in those files only.

`render-repros` is a symlink to the repository's own `render-repros/`, which the
Rust render tests `include_str!` from.
