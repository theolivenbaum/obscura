using System.Globalization;

namespace PocketCalculator.Dom;

/// <summary>
/// A mutation would take the document past <see cref="DomTree.ContentByteBudget"/>. Nothing
/// was changed by the call that throws it.
/// </summary>
public sealed class DomQuotaExceededException(long requested, long used, long budget)
    : Exception($"The document's DOM would exceed its {budget}-byte budget ({used} used, {requested} more requested).")
{
    public long Requested { get; } = requested;

    public long Used { get; } = used;

    public long Budget { get; } = budget;
}

/// <summary>
/// The per-document memory budget (SECURITY.md M7).
/// </summary>
/// <remarks>
/// <para>
/// DEVIATION from crates/obscura-dom, which has no budget: text and attributes live in the
/// arena, outside the V8 heap cap, so a loop appending 4 MB text nodes grew the process past
/// 3 GB until the OS killed it. Chromium has no such limit either; it runs out of memory and
/// kills the renderer. Here every node is charged <see cref="NodeOverheadBytes"/> plus two
/// bytes per UTF-16 code unit of its text, comment, processing-instruction or attribute
/// data, incrementally where the data is created or changed, so a check is O(1). A growth
/// past the budget throws <see cref="DomQuotaExceededException"/> before anything changes;
/// script sees a <c>QuotaExceededError</c> DOMException, the parser stops adding content.
/// </para>
/// <para>
/// A detached node stays charged while anything may still reach it (the JS side may hold its
/// wrapper), because it is still memory the page holds. Freeing the slot gives the bytes
/// back: <see cref="Remove"/>, or the collector in DomTree.Gc.cs once nothing holds the
/// node's component; an op refused by the budget collects and retries once. Host code that writes node data directly (not through
/// <see cref="NewNode"/>, <see cref="AppendText"/> or <see cref="ChargeGrowth"/>) is not
/// counted until the node is freed; the count is clamped at zero.
/// </para>
/// </remarks>
public sealed partial class DomTree
{
    /// <summary>What one node costs the budget beyond its character data.</summary>
    public const int NodeOverheadBytes = 64;

    private long _contentBytes;

    /// <summary>
    /// The budget a new tree gets: <c>POCKETCALCULATOR_MAX_DOM_BYTES</c>, or 512 MiB. Zero
    /// disables it. Read once per process.
    /// </summary>
    public static long DefaultContentByteBudget { get; } = ReadDefaultBudget();

    /// <summary>Bytes charged to this tree so far.</summary>
    public long ContentBytes => _contentBytes;

    /// <summary>The ceiling on <see cref="ContentBytes"/>; zero or less for none.</summary>
    public long ContentByteBudget { get; set; } = DefaultContentByteBudget;

    private static long ReadDefaultBudget()
    {
        string? raw = Environment.GetEnvironmentVariable("POCKETCALCULATOR_MAX_DOM_BYTES");
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes) && bytes >= 0
            ? bytes
            : 512L * 1024 * 1024;
    }

    /// <summary>Whether a parse into this tree stopped at the budget and dropped content.</summary>
    public bool ParseTruncated { get; internal set; }

    /// <summary>What <paramref name="data"/> costs the budget, overhead included.</summary>
    public static long SizeOf(NodeData data)
    {
        long chars = data switch
        {
            TextData text => text.Contents.Length,
            CommentData comment => comment.Contents.Length,
            ElementData element => AttributeChars(element.Attrs),
            ProcessingInstructionData pi => (long)pi.Target.Length + pi.Data.Length,
            DoctypeData doctype => (long)doctype.Name.Length + doctype.PublicId.Length + doctype.SystemId.Length,
            _ => 0,
        };
        return NodeOverheadBytes + (2 * chars);
    }

    /// <summary>What one attribute costs the budget.</summary>
    public static long SizeOf(Attribute attribute) =>
        2 * ((long)attribute.Name.Local.Length + (attribute.Name.Prefix?.Length ?? 0) + attribute.Value.Length);

    private static long AttributeChars(List<Attribute> attrs)
    {
        long chars = 0;
        for (var i = 0; i < attrs.Count; i++)
        {
            var attr = attrs[i];
            chars += attr.Name.Local.Length + (attr.Name.Prefix?.Length ?? 0) + attr.Value.Length;
        }

        return chars;
    }

    /// <summary>
    /// Throws <see cref="DomQuotaExceededException"/> if <paramref name="bytes"/> more would
    /// take the tree past its budget. Charges nothing.
    /// </summary>
    public void EnsureCanGrow(long bytes)
    {
        if (bytes > 0 && ContentByteBudget > 0 && _contentBytes + bytes > ContentByteBudget)
        {
            throw new DomQuotaExceededException(bytes, _contentBytes, ContentByteBudget);
        }
    }

    /// <summary>
    /// Records a change of <paramref name="delta"/> bytes in node data the caller is about to
    /// make, throwing first (and charging nothing) if a growth does not fit.
    /// </summary>
    public void ChargeGrowth(long delta)
    {
        EnsureCanGrow(delta);
        _contentBytes = Math.Max(0, _contentBytes + delta);
    }

    /// <summary>
    /// Records a change of <paramref name="delta"/> bytes already made, after the caller
    /// checked the growth with <see cref="EnsureCanGrow"/>. Never throws.
    /// </summary>
    public void RecordChange(long delta) => _contentBytes = Math.Max(0, _contentBytes + delta);

    /// <summary>What node <paramref name="id"/>'s data costs the budget now, or zero.</summary>
    public long DataSize(NodeId id) => Slot(id) is { } node ? SizeOf(node.Data) : 0;
}
