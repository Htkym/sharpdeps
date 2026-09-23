namespace SharpDeps.Analysis.Core.Cli;

public sealed class CliOptions
{
    private readonly Dictionary<string, string> _values;

    private CliOptions(Dictionary<string, string> values) => _values = values;

    public static CliOptions Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < arguments.Length; index++)
        {
            var token = arguments[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = token[2..];
            var value = index + 1 < arguments.Length && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? arguments[++index]
                : "true";
            values[key] = value;
        }

        return new CliOptions(values);
    }

    public string Require(string key)
        => _values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing required option --{key}");

    public int GetInt(string key, int fallback)
        => _values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : fallback;
}
