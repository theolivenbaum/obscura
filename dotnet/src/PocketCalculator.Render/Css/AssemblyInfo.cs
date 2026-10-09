using System.Runtime.CompilerServices;

// The CSS port keeps the Rust file's private helpers internal. The xUnit port
// of `#[cfg(test)] mod tests` needs the same access an in-file Rust test module
// has, so the test assembly is a friend.
[assembly: InternalsVisibleTo("PocketCalculator.Render.Tests")]

// The script-level layout tests observe whether a read after a write re-laid out the whole
// document (DomLayout.TransplantedBoxes), which no public surface reports.
[assembly: InternalsVisibleTo("PocketCalculator.Js.Tests")]
