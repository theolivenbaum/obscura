using System.Threading.Channels;
using PocketCalculator.Js.Runtime;

namespace PocketCalculator.Cdp;

/// <summary>
/// An awaited <c>Runtime.evaluate</c> / <c>Runtime.callFunctionOn</c> answered after
/// the commands that arrived behind it.
/// </summary>
/// <remarks>
/// Port addition. Chromium serves a connection's other commands while an
/// <c>awaitPromise</c> promise is pending and answers it when it settles, so responses
/// can arrive out of order (clients match them by id). The Rust server awaits inline,
/// which deadlocks a promise that waits for the client: an exposed function answers
/// <c>Runtime.bindingCalled</c> with a command of its own.
/// </remarks>
internal sealed class ParkedCommand(
    ulong id,
    string? sessionId,
    string method,
    ParkedCdpAwait await,
    Func<RemoteObjectInfo, Task<DomainResult>> finish)
{
    public ulong Id { get; } = id;

    public string? SessionId { get; } = sessionId;

    public string Method { get; } = method;

    public ParkedCdpAwait Await { get; } = await;

    public Func<RemoteObjectInfo, Task<DomainResult>> Finish { get; } = finish;

    /// <summary>Where the answer goes; set by the processor once the command has parked.</summary>
    public ChannelWriter<string>? ReplyTx { get; set; }
}

/// <summary>Thrown by a domain handler whose awaited promise parked; the dispatcher records it.</summary>
internal sealed class CommandParkedException(
    ParkedCdpAwait parked,
    Func<RemoteObjectInfo, Task<DomainResult>> finish) : Exception(parked.Method + " parked")
{
    public ParkedCdpAwait Parked { get; } = parked;

    public Func<RemoteObjectInfo, Task<DomainResult>> Finish { get; } = finish;
}
