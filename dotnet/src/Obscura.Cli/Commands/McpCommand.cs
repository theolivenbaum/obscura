using Obscura.Cli.CommandLine;
using Obscura.Mcp;

namespace Obscura.Cli.Commands;

/// <summary>The <c>mcp</c> subcommand: the stdio server, or the HTTP/SSE transport.</summary>
public static class McpCommand
{
    /// <summary>Dispatch for the <c>mcp</c> subcommand.</summary>
    public static async Task RunAsync(CliArgs args, CliCommand.Mcp mcp)
    {
        var proxy = CliOptions.MergeProxy(args.Proxy, mcp.Proxy);
        if (mcp.Http)
        {
            if (mcp.Port is < 0 or > ushort.MaxValue)
            {
                throw new CliException($"--port must be between 0 and 65535, got {mcp.Port}");
            }
            await Http.RunAsync(mcp.Host, (ushort)mcp.Port, proxy, mcp.UserAgent, args.Stealth)
                .ConfigureAwait(false);
            return;
        }
        await McpServer.RunAsync(proxy, mcp.UserAgent, args.Stealth).ConfigureAwait(false);
    }
}
