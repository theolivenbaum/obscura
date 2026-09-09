using Xunit;

namespace Obscura.Parity.Tests.Harness;

/// <summary>The <c>render-repros/</c> corpus, addressed as <c>file://</c> URLs.</summary>
public static class Fixtures
{
    /// <summary>The repository root, or null when the tests run outside a checkout.</summary>
    public static string? RepoRoot { get; } = FindRepoRoot();

    /// <summary>Where <c>scripts/regen-golden.sh</c> writes the recorded corpus.</summary>
    public static string GoldenDirectory =>
        Path.Combine(RepoRoot ?? ".", "dotnet", "tests", "Obscura.Parity.Tests", "golden");

    /// <summary>Every fixture, as (name, file URL), in a stable order.</summary>
    public static IReadOnlyList<(string Name, string Url)> Named()
    {
        if (RepoRoot is null)
        {
            return [];
        }
        var dir = Path.Combine(RepoRoot, "render-repros");
        if (!Directory.Exists(dir))
        {
            return [];
        }
        return [.. Directory.EnumerateFiles(dir, "*.html")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (Path.GetFileNameWithoutExtension(path), $"file://{path}"))];
    }

    /// <summary>Every fixture URL, as theory data.</summary>
    public static TheoryData<string> All()
    {
        var data = new TheoryData<string>();
        foreach (var (_, url) in Named())
        {
            data.Add(url);
        }
        return data;
    }

    /// <summary>
    /// An evenly spread subset, for the modes where running the whole corpus
    /// through two V8 processes each is not worth the wall time.
    /// </summary>
    public static TheoryData<string> Sample(int count)
    {
        var all = Named();
        var data = new TheoryData<string>();
        if (all.Count == 0)
        {
            return data;
        }
        var step = Math.Max(1, all.Count / count);
        for (var i = 0; i < all.Count && data.Count < count; i += step)
        {
            data.Add(all[i].Url);
        }
        return data;
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) && Directory.Exists(Path.Combine(dir, "crates")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }
}
