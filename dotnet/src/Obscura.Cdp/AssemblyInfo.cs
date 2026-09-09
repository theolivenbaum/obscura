using System.Runtime.CompilerServices;

// The CDP tests exercise the same items the Rust `#[cfg(test)] mod tests` blocks
// in server.rs and dispatch.rs reach with `use super::{...}`: the connection
// processor, the navigation router, the header parser, the fetch-resolution
// path, and the execution-context bookkeeping the ownership tests assert on.
[assembly: InternalsVisibleTo("Obscura.Cdp.Tests")]
