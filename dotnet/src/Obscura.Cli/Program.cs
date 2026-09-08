using System.CommandLine;
using Obscura.Cli.CommandLine;

var root = CliDefinition.Build();

// Subcommand behavior is ported per command; until a command is ported it must
// fail loudly rather than exit 0 having done nothing, which would make the CLI
// parity tests pass against an engine that never ran.
foreach (var command in root.Subcommands)
{
    var name = command.Name;
    command.SetAction(_ =>
    {
        Console.Error.WriteLine($"obscura: '{name}' is not implemented in the .NET port yet");
        return 70; // EX_SOFTWARE
    });
}

root.SetAction(_ =>
{
    Console.Error.WriteLine("obscura: no subcommand given (try 'fetch', 'serve', 'scrape' or 'mcp')");
    return 64; // EX_USAGE
});

return root.Parse(args).Invoke();
