namespace PocketCalculator.Js.Ops;

/// <summary>
/// A document's parser as <c>document.write()</c> sees it (port addition; see
/// <see cref="PocketCalculatorState.ParserWriter"/>).
/// </summary>
public interface IDocumentWriteTarget
{
    /// <summary>Whether a parser-inserted script is running, so written text has somewhere to go.</summary>
    bool HasInsertionPoint { get; }

    /// <summary>
    /// Insert <paramref name="text"/> at the insertion point and parse what can be parsed of it
    /// now. False, with nothing done, when there is no insertion point.
    /// </summary>
    bool TryWrite(string text);
}
