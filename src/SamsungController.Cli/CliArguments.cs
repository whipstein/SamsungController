namespace SamsungController.Cli;

internal sealed class CliArguments
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--host",
        "--name",
        "--port",
        "--action",
        "--log",
        "--token-file",
        "--config-dir",
        "--pairing-timeout"
    };

    private static readonly HashSet<string> FlagOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--insecure",
        "--secure",
        "--strict-tls",
        "--quiet",
        "--help"
    };

    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    private CliArguments(string command, IReadOnlyList<string> positionals)
    {
        Command = command;
        Positionals = positionals;
    }

    public string Command { get; }

    public IReadOnlyList<string> Positionals { get; }

    public static CliArguments Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new CliArguments("help", []);
        }

        var command = args[0];
        var positionals = new List<string>();
        var result = new CliArguments(command, positionals);

        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                continue;
            }

            var equalsIndex = argument.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex > 2)
            {
                var name = argument[..equalsIndex];
                if (!ValueOptions.Contains(name))
                {
                    throw new ArgumentException($"Unknown option: {name}");
                }

                result._options[name] = argument[(equalsIndex + 1)..];
                continue;
            }

            if (ValueOptions.Contains(argument))
            {
                if (++index >= args.Length)
                {
                    throw new ArgumentException($"Option {argument} requires a value.");
                }

                result._options[argument] = args[index];
                continue;
            }

            if (!FlagOptions.Contains(argument))
            {
                throw new ArgumentException($"Unknown option: {argument}");
            }

            result._flags.Add(argument);
        }

        return result;
    }

    public string? GetOption(string name) => _options.GetValueOrDefault(name);

    public bool HasFlag(string name) => _flags.Contains(name);
}
