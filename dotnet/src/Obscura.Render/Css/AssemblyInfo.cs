using System.Runtime.CompilerServices;

// The CSS port keeps the Rust file's private helpers internal. The xUnit port
// of `#[cfg(test)] mod tests` needs the same access an in-file Rust test module
// has, so the test assembly is a friend.
[assembly: InternalsVisibleTo("Obscura.Render.Tests")]
