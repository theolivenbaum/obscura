using System.Runtime.CompilerServices;

// The op tests exercise the same internal helpers the Rust `mod tests` reaches
// with `use super::{...}`: the render-invalidation planner, the fetch policy
// gate, and the capped push. Keeping them internal keeps the public op surface
// to the ops themselves.
[assembly: InternalsVisibleTo("Obscura.Js.Tests")]
