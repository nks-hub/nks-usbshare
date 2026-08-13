namespace NksUsbShare.Cli;

/// <summary>Minimal argument parsing: a verb, positional arguments, and --flags.</summary>
internal sealed record CommandLine(
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlySet<string> Flags,
    IReadOnlyDictionary<string, string> Values,
    string? Home)
{
    /// <summary>Switches that take a value rather than standing on their own.</summary>
    private static readonly string[] TakesValue = ["home", "port", "log"];

    /// <summary>The value given for a switch, or <paramref name="fallback"/> when it was absent.</summary>
    public int Number(string name, int fallback)
        => Values.TryGetValue(name, out var text) && int.TryParse(text, out var value) ? value : fallback;

    public static CommandLine Parse(string[] args)
    {
        var positional = new List<string>();
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (!argument.StartsWith('-'))
            {
                positional.Add(argument);
                continue;
            }

            var name = argument.TrimStart('-');
            if (TakesValue.Contains(name, StringComparer.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                values[name] = args[++i];
                continue;
            }

            flags.Add(name);
        }

        var command = positional.Count > 0 ? positional[0] : "help";
        if (flags.Contains("help") || flags.Contains("h")) command = "help";
        if (flags.Contains("version")) command = "version";

        values.TryGetValue("home", out var home);

        return new CommandLine(command.ToLowerInvariant(), positional.Skip(1).ToArray(), flags, values, home);
    }
}
