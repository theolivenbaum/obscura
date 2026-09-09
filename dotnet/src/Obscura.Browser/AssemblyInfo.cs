using System.Runtime.CompilerServices;

// The browser tests exercise the same free functions the Rust `mod tests` reaches
// with `use super::{...}`: the CSS scanners, the import splitter, the navigation
// env parsers and the CDP URL pattern matcher.
[assembly: InternalsVisibleTo("Obscura.Browser.Tests")]
