namespace Tommy.Core.Configuration;

/// <summary>
/// Parses the Settings "environment variables" text box — one <c>KEY=VALUE</c>
/// per line — into the map stored on <see cref="McpServerEntry.Env"/>. Blank
/// lines and lines beginning with <c>#</c> are ignored; the first <c>=</c>
/// splits key from value (so a value may itself contain <c>=</c>); a single pair
/// of surrounding quotes on the value is stripped; a later definition of the same
/// key wins. A line without a key (no <c>=</c>, or <c>=value</c>) is skipped.
/// </summary>
public static class McpEnvText
{
    public static Dictionary<string, string> Parse(string? text)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return env;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue; // no '=', or an empty key

            var key = line[..eq].Trim();
            if (key.Length == 0) continue;

            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value[1..^1];

            env[key] = value;
        }

        return env;
    }
}
