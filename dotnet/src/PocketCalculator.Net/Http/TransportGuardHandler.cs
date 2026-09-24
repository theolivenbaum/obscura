using System.Net;
using System.Net.Sockets;

namespace PocketCalculator.Net;

/// <summary>
/// The last checks every request on a <see cref="PocketCalculatorHttpClient"/>'s
/// transport passes, whichever path built it: navigations and subresources, each of
/// their redirect hops, and the <c>op_fetch_url</c> requests (fetch, XHR, iframe
/// documents, dynamic scripts) that share the client's <c>RequestClient</c>.
/// </summary>
/// <remarks>
/// <para>
/// Tracker blocking. Deviation from <c>client.rs</c>, whose blocklist covers the first
/// URL of the main transport only (SECURITY.md L7): a blocked host is refused here
/// with Chromium's <c>net::ERR_BLOCKED_BY_CLIENT</c>, so a scripted fetch rejects the
/// way it does under a content blocker. The navigation path answers a blocked hop with
/// its status-0 response before it gets this far.
/// </para>
/// <para>
/// Proxied targets. Deviation from <c>client.rs</c> (SECURITY.md M2): behind a proxy
/// the connect-time guard sees only the proxy's address and the proxy resolves the
/// target, so a public-looking name for an internal address went through unchecked.
/// Unless private network access is allowed, the target is resolved here first and
/// refused when any address is forbidden, or when it does not resolve at all, since a
/// name only the proxy can resolve is exactly an internal one. The residual gap is
/// rebinding: the proxy does its own lookup, which can answer differently from this
/// one. A proxy the operator does not trust to refuse internal targets itself should
/// not be configured for untrusted content.
/// </para>
/// </remarks>
/// <para>
/// HSTS (SECURITY.md I7). The <c>Strict-Transport-Security</c> header of every https
/// response is recorded in the client's <see cref="HstsStore"/>, whichever path sent the
/// request; this handler only sees responses that passed certificate validation. It is
/// also the backstop for the upgrade: the request paths rewrite a known host's http URL
/// to https themselves (and report it as a redirect), and anything that reaches here
/// still on http is rewritten silently.
/// </para>
internal sealed class TransportGuardHandler(
    PocketCalculatorHttpClient owner,
    bool proxied,
    HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } requested && owner.Hsts.Upgrade(requested) is { } upgraded)
        {
            request.RequestUri = upgraded;
        }

        if (request.RequestUri is { } target)
        {
            if (owner.IsBlockedTracker(target))
            {
                throw new HttpRequestException("net::ERR_BLOCKED_BY_CLIENT");
            }

            if (proxied && !owner.AllowPrivateNetwork && !SsrfGuard.EnvAllowsPrivateNetwork())
            {
                await VetProxiedTargetAsync(target, cancellationToken).ConfigureAwait(false);
            }
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.RequestUri is { } answered
            && string.Equals(answered.Scheme, "https", StringComparison.Ordinal)
            && response.Headers.NonValidated.TryGetValues("Strict-Transport-Security", out var sts))
        {
            // RFC 6797 8.1: only the first header is processed.
            foreach (var value in sts)
            {
                owner.Hsts.ProcessHeader(answered, value);
                break;
            }
        }

        return response;
    }

    private async Task VetProxiedTargetAsync(Uri target, CancellationToken cancellationToken)
    {
        var host = target.IdnHost;
        if (host.Length > 2 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await owner.ProxyTargetResolver(host, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException error)
            {
                throw new HttpRequestException(
                    $"SSRF blocked: '{host}' could not be resolved to vet it before proxying", error);
            }
        }

        if (addresses.Length == 0)
        {
            throw new HttpRequestException(
                $"SSRF blocked: '{host}' could not be resolved to vet it before proxying");
        }

        foreach (var address in addresses)
        {
            if (SsrfGuard.IsForbiddenIp(address))
            {
                throw new HttpRequestException(
                    $"SSRF blocked: '{host}' resolves to forbidden address {address}");
            }
        }
    }
}
