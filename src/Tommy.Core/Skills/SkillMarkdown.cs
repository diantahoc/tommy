namespace Tommy.Core.Skills;

/// <summary>Parses a SKILL.md: optional YAML-ish frontmatter (name, description) + body.</summary>
public static class SkillMarkdown
{
    public static (string Name, string Description, string Body) Parse(string text, string fallbackName)
    {
        var name = fallbackName;
        var description = string.Empty;
        var body = text;

        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("---", StringComparison.Ordinal))
        {
            var afterOpen = trimmed.IndexOf('\n');
            if (afterOpen > 0)
            {
                var close = trimmed.IndexOf("\n---", afterOpen, StringComparison.Ordinal);
                if (close > 0)
                {
                    var front = trimmed[(afterOpen + 1)..close];
                    var rest = trimmed[(close + 4)..];
                    body = rest.TrimStart('\r', '\n');

                    foreach (var line in front.Split('\n'))
                    {
                        var idx = line.IndexOf(':');
                        if (idx <= 0) continue;
                        var key = line[..idx].Trim().ToLowerInvariant();
                        var val = line[(idx + 1)..].Trim().Trim('"', '\'');
                        if (key == "name" && val.Length > 0) name = val;
                        else if (key == "description") description = val;
                    }
                }
            }
        }

        return (name, description, body.Trim());
    }
}
