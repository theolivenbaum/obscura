namespace Obscura.Render;

/// <summary>
/// Operator-supplied font directories: the process-wide fonts that <c>serve --font-dir</c>
/// adds beside the embedded faces.
/// </summary>
/// <remarks>
/// Port of <c>configure_font_directories</c> and <c>FONT_DIRECTORIES</c> in
/// <c>crates/obscura-render/src/inline.rs</c> (upstream 343fdc7). Off by default: with nothing
/// configured no file is read and every render pass loads exactly the embedded faces, so layout
/// stays deterministic across hosts. The setting is process-wide and set once, before the first
/// render, the way the Rust <c>OnceLock</c> is: the first <see cref="TextEngine"/> freezes it.
/// <para>
/// Upstream holds the directory faces in a cached base font database. The port reads the files
/// once (on first use) and keeps their bytes here; each pass then loads them into its own
/// <see cref="FontDatabase"/> after the embedded faces, the same way it loads the embedded faces
/// themselves. Upstream's cross-document database cache is not ported.
/// </para>
/// </remarks>
public static class FontDirectories
{
    private static readonly Lock Gate = new();
    private static IReadOnlyList<string>? _configured;
    private static FontDirectorySet? _loaded;

    /// <summary>
    /// Configure the directories to load fonts from. Returns false when fonts have already been
    /// configured or a render has already initialized them, matching the Rust return value.
    /// </summary>
    public static bool Configure(IReadOnlyList<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        lock (Gate)
        {
            if (_loaded is not null || _configured is not null)
            {
                return false;
            }

            _configured = [.. directories];
            return true;
        }
    }

    /// <summary>The configured directories, or null when none have been configured.</summary>
    public static IReadOnlyList<string>? Configured
    {
        get
        {
            lock (Gate)
            {
                return _configured;
            }
        }
    }

    /// <summary>
    /// The configured fonts, read from disk on the first call and frozen from then on. Empty
    /// when nothing was configured.
    /// </summary>
    internal static FontDirectorySet Current
    {
        get
        {
            FontDirectorySet? loaded = Volatile.Read(ref _loaded);
            if (loaded is not null)
            {
                return loaded;
            }

            lock (Gate)
            {
                // `FONT_DIRECTORIES.get_or_init(Vec::new)`: a render with nothing configured
                // closes the setting, so a later Configure reports false.
                _loaded ??= _configured is { Count: > 0 } directories
                    ? FontDirectorySet.Load(directories)
                    : FontDirectorySet.Empty;
                return _loaded;
            }
        }
    }
}

/// <summary>The font files found under a set of directories, read into memory once.</summary>
public sealed class FontDirectorySet
{
    private FontDirectorySet(IReadOnlyList<string> files, IReadOnlyList<byte[]> data)
    {
        Files = files;
        Data = data;
    }

    /// <summary>No directories: the default, which adds nothing to a render pass.</summary>
    public static FontDirectorySet Empty { get; } = new([], []);

    /// <summary>The files that were read, in load order.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>Each file's bytes, parallel to <see cref="Files"/>.</summary>
    public IReadOnlyList<byte[]> Data { get; }

    public bool IsEmpty => Data.Count == 0;

    /// <summary>
    /// Recursively collect TTF, TTC, OTF and OTC files under <paramref name="directories"/> and
    /// read them.
    /// </summary>
    /// <remarks>
    /// Port of <c>load_font_directories</c>. The extension match is case-insensitive; symbolic
    /// links are skipped, file or directory, so a link cycle cannot recurse forever; files are
    /// sorted by path and deduplicated so the load order (the fallback order) does not depend on
    /// directory enumeration order. A directory that cannot be listed and a file that cannot be
    /// read are skipped silently, as upstream does, and a file that does not parse as a font
    /// contributes no face. Paths are made absolute before sorting and deduplicating, so the
    /// same directory named twice (relative and absolute) loads its files once.
    /// </remarks>
    public static FontDirectorySet Load(IEnumerable<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            // Hidden files are fonts too; only links are skipped.
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        Stack<string> pending = new();
        foreach (string directory in directories)
        {
            pending.Push(directory);
        }

        List<string> files = [];
        while (pending.TryPop(out string? directory))
        {
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = [.. new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options)];
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or System.Security.SecurityException or ArgumentException)
            {
                continue;
            }

            foreach (FileSystemInfo entry in entries)
            {
                if (entry.LinkTarget is not null)
                {
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                }
                else if (entry is FileInfo && IsFontExtension(entry.Extension))
                {
                    files.Add(Path.GetFullPath(entry.FullName));
                }
            }
        }

        files.Sort(ComparePathComponents);
        List<string> loadedFiles = [];
        List<byte[]> data = [];
        string? previous = null;
        foreach (string path in files)
        {
            if (previous is not null && string.Equals(previous, path, StringComparison.Ordinal))
            {
                continue;
            }

            previous = path;
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                continue;
            }

            // fontdb parses raw sfnt and collections only. FontDatabase.LoadFontSource would
            // also unwrap WOFF, which is a page-font format; keep a renamed .woff out so the
            // directory loads exactly what upstream loads.
            if (bytes.AsSpan().StartsWith("wOFF"u8) || bytes.AsSpan().StartsWith("wOF2"u8))
            {
                continue;
            }

            loadedFiles.Add(path);
            data.Add(bytes);
        }

        return new FontDirectorySet(loadedFiles, data);
    }

    private static bool IsFontExtension(string extension) =>
        extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".otf", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".otc", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>PathBuf</c>'s ordering: component by component, each compared by its bytes, so
    /// <c>a/b/x</c> sorts before <c>a/b-c/x</c> where a plain string compare would not.
    /// </summary>
    private static int ComparePathComponents(string left, string right)
    {
        string[] a = left.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        string[] b = right.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        int count = Math.Min(a.Length, b.Length);
        for (int i = 0; i < count; i++)
        {
            int order = string.CompareOrdinal(a[i], b[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return a.Length.CompareTo(b.Length);
    }
}
