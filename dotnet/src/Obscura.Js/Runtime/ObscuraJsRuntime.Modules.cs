using System.Diagnostics;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Obscura.Js.Modules;

namespace Obscura.Js.Runtime;

/// <summary>
/// ES-module loading and evaluation.
/// </summary>
/// <remarks>
/// <para>
/// The reference separates a graph <em>load</em> (fetch and instantiate, bounded
/// by its own budget) from its <em>evaluation</em> (deliberately delayed until
/// the HTML script scheduler reaches its post-parse turn). deno_core exposes
/// both halves; ClearScript does not. Its module entry point compiles,
/// instantiates and evaluates in one call, and dependency documents are fetched
/// lazily from inside it.
/// </para>
/// <para>
/// So the split is preserved in shape but not in timing: <c>Prepare</c> fetches
/// the entry source and reserves a module id, and <c>Evaluate</c> runs the whole
/// graph. The observable consequences are recorded in the report rather than
/// papered over: a dependency fetch failure surfaces at evaluation rather than
/// at prepare, and the load budget cannot bound the dependency fetches on its
/// own.
/// </para>
/// <para>
/// What is preserved exactly: evaluation is idempotent per module id and per
/// entry specifier, and a dependency already evaluated as part of another
/// graph is a browser-style no-op when it later shows up as a top-level script.
/// </para>
/// </remarks>
public sealed partial class ObscuraJsRuntime
{
    private long _nextModuleId = 1;
    private readonly Dictionary<long, string> _preparedSources = [];

    /// <summary>Fetch and evaluate a module graph within one budget.</summary>
    public async Task LoadModuleAsync(string url, ulong budgetMs)
    {
        var clock = Stopwatch.StartNew();
        var prepared = await PrepareModuleAsync(url, budgetMs).ConfigureAwait(false);
        var remaining = RemainingBudgetMs(clock, budgetMs)
            ?? throw new JsRuntimeException($"Module {url} exhausted its {budgetMs}ms load+evaluation budget");
        await EvaluatePreparedModuleAsync(prepared, remaining).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetch a module graph's entry without evaluating it. The caller sizes the
    /// budget: short for enhancement modules on an already-rendered page, full
    /// for an unmounted SPA shell (#205).
    /// </summary>
    public async Task<PreparedModule> PrepareModuleAsync(string url, ulong budgetMs)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var specifier))
        {
            throw new JsRuntimeException($"Invalid module URL {url}: relative URL without a base");
        }
        var loadedStart = _moduleLoader.LoadedSpecifiers.Count;
        var source = await FetchModuleSourceAsync(specifier.ToString(), budgetMs, url).ConfigureAwait(false);

        var graphSpecifiers = SpecifiersSince(loadedStart).ToList();
        graphSpecifiers.Add(specifier.ToString());
        graphSpecifiers.Sort(StringComparer.Ordinal);

        var moduleId = _nextModuleId++;
        _preparedSources[moduleId] = source;
        return new PreparedModule
        {
            ModuleId = moduleId,
            Description = $"Module {url}",
            EntrySpecifier = specifier.ToString(),
            GraphSpecifiers = graphSpecifiers.Distinct(StringComparer.Ordinal).ToArray(),
        };
    }

    /// <summary>Load and evaluate an inline <c>&lt;script type=module&gt;</c>.</summary>
    public async Task LoadInlineModuleAsync(string code, string baseUrl, ulong budgetMs)
    {
        var clock = Stopwatch.StartNew();
        var prepared = await PrepareInlineModuleAsync(code, baseUrl, budgetMs).ConfigureAwait(false);
        var remaining = RemainingBudgetMs(clock, budgetMs)
            ?? throw new JsRuntimeException($"Inline module exhausted its {budgetMs}ms load+evaluation budget");
        await EvaluatePreparedModuleAsync(prepared, remaining).ConfigureAwait(false);
    }

    /// <summary>
    /// Prepare an inline module. Inline modules use the document base URL as
    /// their module URL, which is observable through <c>import.meta.url</c> and
    /// is also the referrer for relative imports and import-map scope matching.
    /// Several inline modules deliberately share that URL; each keeps its own
    /// source and module id.
    /// </summary>
    public Task<PreparedModule> PrepareInlineModuleAsync(string code, string baseUrl, ulong budgetMs)
    {
        _ = budgetMs;
        var specifier = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            ? uri.ToString()
            : "about:blank";
        var loadedStart = _moduleLoader.LoadedSpecifiers.Count;
        var moduleId = _nextModuleId++;
        _preparedSources[moduleId] = code;
        var graphSpecifiers = SpecifiersSince(loadedStart).ToList();
        graphSpecifiers.Sort(StringComparer.Ordinal);
        return Task.FromResult(new PreparedModule
        {
            ModuleId = moduleId,
            Description = "Inline module",
            EntrySpecifier = null,
            GraphSpecifiers = graphSpecifiers.Distinct(StringComparer.Ordinal).ToArray(),
        });
    }

    /// <summary>
    /// Evaluate a prepared module, bounded by both an async deadline and a hard
    /// V8 watchdog.
    /// </summary>
    /// <remarks>
    /// The watchdog is not redundant: an async timeout cannot run while
    /// synchronous top-level module work pins the thread inside V8, so pairing
    /// the two is what makes the budget a real wall-clock ceiling for both
    /// forms of evaluation.
    /// </remarks>
    public async Task EvaluatePreparedModuleAsync(PreparedModule prepared, ulong budgetMs)
    {
        ArgumentNullException.ThrowIfNull(prepared);

        if (prepared.EntrySpecifier is { } entry
            && _evaluatedModuleSpecifiers.TryGetValue(entry, out var known))
        {
            Rethrow(known);
            return;
        }

        var watchdog = ArmWatchdog(TimeSpan.FromMilliseconds(budgetMs));
        string? outcome;
        try
        {
            outcome = await DriveModuleEvalAsync(prepared, budgetMs).ConfigureAwait(false);
        }
        finally
        {
            if (DisarmWatchdog(watchdog))
            {
                outcome = $"{prepared.Description} evaluation timed out after {budgetMs}ms";
            }
        }

        if (prepared.EntrySpecifier is { } specifier)
        {
            _evaluatedModuleSpecifiers[specifier] = outcome;
        }
        if (outcome is null)
        {
            foreach (var dependency in prepared.GraphSpecifiers)
            {
                _evaluatedModuleSpecifiers[dependency] = null;
            }
        }
        Rethrow(outcome);
    }

    /// <summary>
    /// Drive a module evaluation to completion, or up to the budget. Returns
    /// null on success and the failure message otherwise.
    /// </summary>
    /// <remarks>
    /// Browser module-map evaluation is idempotent, so the first outcome for a
    /// module id is retained and replayed for duplicate script tags and roots
    /// already seen.
    /// </remarks>
    private async Task<string?> DriveModuleEvalAsync(PreparedModule prepared, ulong budgetMs)
    {
        if (_moduleEvaluations.TryGetValue(prepared.ModuleId, out var cached))
        {
            return cached;
        }

        BeginJavaScriptTask();
        var source = _preparedSources.GetValueOrDefault(prepared.ModuleId, string.Empty);
        var name = prepared.EntrySpecifier ?? _moduleLoader.BaseUrl;
        var info = Uri.TryCreate(name, UriKind.Absolute, out var uri)
            ? new DocumentInfo(uri) { Category = ModuleCategory.Standard }
            : new DocumentInfo(name) { Category = ModuleCategory.Standard };

        string? outcome;
        var clock = Stopwatch.StartNew();
        // Every load that arrives inside this bracket belongs to a statically
        // declared graph; anything outside it is an import() continuation, which
        // is the distinction deno_core gets from is_dyn_import and ClearScript
        // does not report at all.
        using var staticGraph = _moduleLoader.BeginStaticGraph();
        try
        {
            _engine.Execute(info, source);
            // Top-level await leaves the module's promise pending; the graph is
            // only finished once the loop has drained what it queued.
            var remaining = budgetMs > (ulong)clock.ElapsedMilliseconds
                ? budgetMs - (ulong)clock.ElapsedMilliseconds
                : 0;
            if (remaining > 0)
            {
                await RunEventLoopBoundedAsync(Math.Min(remaining, budgetMs)).ConfigureAwait(false);
            }
            outcome = null;
        }
        catch (ScriptInterruptedException)
        {
            outcome = $"{prepared.Description} evaluation timed out after {budgetMs}ms";
        }
        catch (ScriptEngineException error)
        {
            outcome = $"{prepared.Description} eval error: {error.Message}";
        }
        catch (JsRuntimeException error)
        {
            outcome = $"{prepared.Description} eval error: {error.Message}";
        }

        _moduleEvaluations[prepared.ModuleId] = outcome;
        return outcome;
    }

    /// <summary>
    /// Fetch the entry module's source through the page-scoped loader, so the
    /// entry travels the same transport as its dependencies: cookies, configured
    /// headers, redirects, interception and callbacks must not change at the
    /// first import edge.
    /// </summary>
    private async Task<string> FetchModuleSourceAsync(string url, ulong budgetMs, string reported)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(budgetMs));
        var load = _moduleLoader.LoadDocumentAsync(
            _engine.DocumentSettings, null, url, ModuleCategory.Standard, _ => null!);
        Task completed;
        try
        {
            completed = await Task.WhenAny(load, Task.Delay(Timeout.Infinite, cancel.Token))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new JsRuntimeException($"Module graph load timed out after {budgetMs}ms: {reported}");
        }
        if (completed != load)
        {
            throw new JsRuntimeException($"Module graph load timed out after {budgetMs}ms: {reported}");
        }
        try
        {
            var document = await load.ConfigureAwait(false);
            return document is StringDocument text
                ? text.StringContents
                : new StreamReader(document.Contents).ReadToEnd();
        }
        catch (Exception error)
        {
            throw new JsRuntimeException($"Module load error: {error.Message}");
        }
    }

    private string[] SpecifiersSince(int start)
    {
        var loaded = _moduleLoader.LoadedSpecifiers;
        return start >= loaded.Count ? [] : loaded.Skip(start).ToArray();
    }

    private static ulong? RemainingBudgetMs(Stopwatch clock, ulong budgetMs)
    {
        var spent = (ulong)Math.Max(0, clock.ElapsedMilliseconds);
        // Round up so a positive sub-millisecond remainder still gets one
        // bounded event-loop turn; the watchdog supplies the hard boundary.
        return spent >= budgetMs ? null : budgetMs - spent;
    }

    private static void Rethrow(string? outcome)
    {
        if (outcome is not null)
        {
            throw new JsRuntimeException(outcome);
        }
    }

    /// <summary>
    /// The page-scoped module loader, shared with the import map a
    /// <c>&lt;script type="importmap"&gt;</c> merges into.
    /// </summary>
    public ObscuraModuleLoader ModuleLoader => _moduleLoader;
}
