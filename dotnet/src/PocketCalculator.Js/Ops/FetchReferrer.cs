using PocketCalculator.Js.Url;
using PocketCalculator.Net;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// The referrer settings of one <c>op_fetch_url</c> request: fetch()'s <c>referrerPolicy</c>
/// (or the loading element's <c>referrerpolicy</c>) and its <c>referrer</c>. Port addition:
/// Rust sends no Referer on these requests.
/// </summary>
/// <remarks>
/// The shim passes them in the op's fifth argument, the <c>origin</c> slot the host has
/// ignored since SECURITY.md C1, as <c>ref\n{policy}\n{referrer}</c>: an empty policy means
/// the document's, and the referrer is <c>about:client</c> (the document), the empty
/// string (no referrer) or a URL. Any other value (an origin from an older caller) means the
/// defaults. Both parts are page-controlled, and neither widens anything: a referrer URL
/// from another origin falls back to the document, as Chromium does.
/// </remarks>
public sealed record FetchReferrer(ReferrerPolicy? Policy, string Referrer)
{
    private const string Prefix = "ref\n";

    /// <summary>
    /// Whether <see cref="Referrer"/> was named by the host rather than by page script (a
    /// stylesheet importing another names itself), so it is used even when it is of another
    /// origin than the document. <see cref="Parse"/> never sets it.
    /// </summary>
    public bool Trusted { get; init; }

    /// <summary>The defaults: the document's policy and the document as referrer.</summary>
    public static readonly FetchReferrer Client = new(null, "about:client");

    /// <summary>Read the op argument; null for the defaults.</summary>
    public static FetchReferrer? Parse(string? argument)
    {
        if (argument is null || !argument.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = argument[Prefix.Length..];
        var newline = rest.IndexOf('\n', StringComparison.Ordinal);
        var policy = newline < 0 ? rest : rest[..newline];
        var referrer = newline < 0 ? "about:client" : rest[(newline + 1)..];
        return new FetchReferrer(
            string.Equals(policy, "no-referrer", StringComparison.Ordinal)
                ? ReferrerPolicy.NoReferrer
                : ReferrerPolicies.ParseAttribute(policy),
            referrer);
    }

    /// <summary>
    /// The URL the Referer is derived from for a request by <paramref name="document"/>, or
    /// null for none.
    /// </summary>
    public Uri? Source(PocketCalculatorState document)
    {
        ArgumentNullException.ThrowIfNull(document);
        // A srcdoc document refers as its parent does (port addition).
        var documentUrl = document.ReferrerSourceUrl ?? document.HistoryUrl ?? document.Url;
        Uri.TryCreate(documentUrl, UriKind.Absolute, out var client);
        if (Referrer.Length == 0)
        {
            return null;
        }

        if (Trusted)
        {
            return Uri.TryCreate(Referrer, UriKind.Absolute, out var named) ? named : client;
        }

        if (string.Equals(Referrer, "about:client", StringComparison.Ordinal)
            || UrlRecord.Parse(Referrer) is not { } record)
        {
            return client;
        }

        // Fetch: a referrer URL of another origin is replaced by the client.
        return string.Equals(record.AsciiOrigin, StateHelpers.DocumentOrigin(document), StringComparison.Ordinal)
            && Uri.TryCreate(record.Href, UriKind.Absolute, out var parsed)
                ? parsed
                : client;
    }
}
