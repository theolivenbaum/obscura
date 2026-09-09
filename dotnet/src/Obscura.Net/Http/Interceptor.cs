namespace Obscura.Net;

/// <summary>
/// What an interceptor decided to do with a request (port of
/// <c>crates/obscura-net/src/interceptor.rs</c>).
/// </summary>
public abstract record InterceptAction
{
    private InterceptAction()
    {
    }

    /// <summary>Let the request go to the network unchanged.</summary>
    public sealed record Continue : InterceptAction
    {
        /// <summary>The shared instance.</summary>
        public static Continue Instance { get; } = new();
    }

    /// <summary>Fail the request with <c>ObscuraNetError::Blocked</c>.</summary>
    public sealed record Block : InterceptAction
    {
        /// <summary>The shared instance.</summary>
        public static Block Instance { get; } = new();
    }

    /// <summary>Answer the request locally with this response.</summary>
    public sealed record Fulfill(Response Response) : InterceptAction;

    /// <summary>Merge these headers into the client's extra headers and continue.</summary>
    public sealed record ModifyHeaders(IReadOnlyDictionary<string, string> Headers) : InterceptAction;
}

/// <summary>A request interceptor installed on <see cref="ObscuraHttpClient"/>.</summary>
public interface IRequestInterceptor
{
    /// <summary>Decide what to do with <paramref name="request"/>.</summary>
    Task<InterceptAction> InterceptAsync(RequestInfo request);
}
