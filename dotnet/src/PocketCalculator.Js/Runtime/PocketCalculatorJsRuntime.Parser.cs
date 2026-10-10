using System.Text;
using Microsoft.ClearScript;
using PocketCalculator.Dom;

namespace PocketCalculator.Js.Runtime;

/// <summary>
/// What a document's parser needs from the runtime while page script runs between its steps.
/// </summary>
/// <remarks>
/// DEVIATION from crates/obscura-js, whose document is parsed whole before the runtime runs any
/// of it. The parser now inserts nodes natively while the realm is live, so the realm is told
/// what changed before its script runs again, as the DOM insertion steps would have told it:
/// its tree caches, window named access, MutationObservers, custom element upgrades, and the
/// frames and preload links that start loading when they are inserted.
/// </remarks>
public sealed partial class PocketCalculatorJsRuntime
{
    /// <summary>
    /// Tell the page realm about the nodes the parser inserted (<paramref name="inserted"/>, in
    /// insertion order) since script last ran.
    /// </summary>
    public void ParserInserted(IReadOnlyList<NodeId> inserted)
    {
        ArgumentNullException.ThrowIfNull(inserted);
        if (inserted.Count == 0 || State.Dom is not { } dom || _shim.HostHelpers is not { } helpers)
        {
            return;
        }

        // What a connected DOM mutation op invalidates (DomOps): layout and style built before
        // these nodes existed, and the caches keyed on the activity generation (the document
        // base URL, the meta referrer policy). Their CSS animations start with the document,
        // as they did when the whole document was parsed before the realm started.
        State.ActivityGeneration = unchecked(State.ActivityGeneration + 1);
        State.PreparedRender = null;
        State.PendingStyleMutations.Clear();
        State.ResolvedScroll = null;
        State.ReferrerPolicyCache = null;

        var records = new StringBuilder(inserted.Count * 12);
        var names = new StringBuilder();
        StringBuilder? custom = null;
        StringBuilder? frames = null;
        StringBuilder? preloads = null;
        foreach (var id in inserted)
        {
            if (dom.GetNode(id) is not { } node || node.Parent is not { } parent)
            {
                continue;
            }

            dom.NoteExposed(id);
            records.Append(parent.Raw).Append(',').Append(id.Raw).Append(',');
            if (node.AsElement() is not { } element)
            {
                continue;
            }

            var local = element.Name.Local;
            var html = string.Equals(element.Name.Ns, Namespaces.Html, StringComparison.Ordinal);
            if (node.GetAttribute("id") is { Length: > 0 } elementId)
            {
                names.Append(elementId).Append('\0');
            }

            if (html && local is "embed" or "form" or "iframe" or "img" or "object"
                && node.GetAttribute("name") is { Length: > 0 } name)
            {
                names.Append(name).Append('\0');
            }

            if (local.Contains('-', StringComparison.Ordinal) || (html && node.GetAttribute("is") is not null))
            {
                (custom ??= new()).Append(id.Raw).Append(',');
            }

            if (!html)
            {
                continue;
            }

            if (string.Equals(local, "iframe", StringComparison.Ordinal))
            {
                (frames ??= new()).Append(id.Raw).Append(',');
            }
            else if (string.Equals(local, "link", StringComparison.Ordinal) && node.GetAttribute("rel") is { } rel
                && (rel.Contains("preload", StringComparison.OrdinalIgnoreCase)))
            {
                (preloads ??= new()).Append(id.Raw).Append(',');
            }
        }

        if (records.Length == 0)
        {
            return;
        }

        try
        {
            helpers.InvokeMethod(
                "parserInserted",
                records.ToString(),
                names.ToString(),
                custom?.ToString() ?? string.Empty,
                frames?.ToString() ?? string.Empty,
                preloads?.ToString() ?? string.Empty);
        }
        catch (ScriptEngineException)
        {
            // A broken page must not stop the parse.
        }

        NoteParserMutationForWorlds(dom, inserted);
    }

    /// <summary>The isolated worlds over the page's document learn of the parser's insertions too.</summary>
    private void NoteParserMutationForWorlds(DomTree dom, IReadOnlyList<NodeId> inserted)
    {
        if (_worldsPerDocument.GetValueOrDefault(0u) == 0)
        {
            return;
        }

        foreach (var world in _worlds.Values)
        {
            if (world.FrameId != 0)
            {
                continue;
            }

            foreach (var id in inserted)
            {
                if (dom.GetNode(id)?.Parent is not { } parent)
                {
                    continue;
                }

                try
                {
                    world.NoteExternalMutation(
                        true,
                        new WorldMutationRecord("childList", parent.Raw, id.Raw.ToString(System.Globalization.CultureInfo.InvariantCulture), string.Empty, null, null));
                }
                catch (ScriptEngineException)
                {
                    // A realm that cannot take the record does not undo the insertion.
                }
            }
        }
    }

    /// <summary>Records parser-inserted scripts as already started without a trip into the realm.</summary>
    public void MarkScriptsStarted(IEnumerable<NodeId> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        foreach (var id in scripts)
        {
            State.AlreadyStartedScripts.Add(id);
        }
    }

    /// <summary>
    /// Run the page work that is due now (posted tasks, due timers, one microtask checkpoint
    /// after each), without waiting for anything. True when a task ran.
    /// </summary>
    /// <remarks>
    /// The HTML event loop runs while the parser waits for a script, between a parser yield
    /// and the next tokens, and while the deferred scripts load. The task is bounded like any
    /// other page task, by the synchronous task floor.
    /// </remarks>
    public bool RunDueTasks()
    {
        BeginJavaScriptTask();
        var token = ArmWatchdog(TimeSpan.FromMilliseconds(SynchronousTaskFloorMs + WatchdogSchedulingMarginMs));
        try
        {
            var tick = PumpTick(out _);
            PerformMicrotaskCheckpoint();
            return tick == LoopTick.Progressed;
        }
        catch (ScriptInterruptedException)
        {
            return true;
        }
        finally
        {
            DisarmWatchdog(token);
        }
    }

    /// <summary>Fire <paramref name="type"/> (load, error) at a parser-inserted script element.</summary>
    public void DispatchScriptEvent(NodeId script, string type)
    {
        if (_shim.HostHelpers is not { } helpers || State.Dom is not { } dom || dom.GetNode(script) is null)
        {
            return;
        }

        dom.NoteExposed(script);
        try
        {
            helpers.InvokeMethod("scriptEvent", (double)script.Raw, type);
        }
        catch (ScriptEngineException)
        {
            // A listener that throws is reported by the dispatch itself.
        }
    }

    /// <summary>
    /// The document this runtime was created for turned out to be at <paramref name="url"/>
    /// (a runtime created while its navigation was still being fetched, which then redirected):
    /// module specifiers resolve against it.
    /// </summary>
    public void RebaseModuleLoader(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        _moduleLoader.BaseUrl = url;
    }

    /// <summary>Milliseconds until the next timer is due; null with none armed.</summary>
    public double? NextTimerDelayMs => Timers.NextDelayMs();
}
