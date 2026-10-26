using System.Text.Json;
using System.Text.RegularExpressions;

namespace AudioMemo.Core.AI;

public sealed record CopilotConfiguration(string? Model, IReadOnlyList<string> RecentModels)
{
    public static JsonDocumentOptions DocumentOptions => new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static CopilotConfiguration Parse(string json)
    {
        using var document = JsonDocument.Parse(json, DocumentOptions);
        var root = document.RootElement;
        string? model = null;
        if (root.TryGetProperty("model", out var configured) && configured.ValueKind == JsonValueKind.String)
            model = configured.GetString();
        var recent = new List<string>();
        if (root.TryGetProperty("recentModelIds", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
                if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } id)
                    recent.Add(id);
        }
        return new(model, recent.Distinct().ToArray());
    }

    public static IReadOnlyList<string> ParseModelHelp(string help)
    {
        var lines = help.Split('\n');
        var modelSection = false;
        var models = new List<string>();
        foreach (var line in lines)
        {
            if (line.Contains("`model`:", StringComparison.Ordinal))
            {
                modelSection = true;
                continue;
            }
            if (!modelSection)
                continue;
            var match = Regex.Match(line, "^\\s+-\\s+\"([a-zA-Z0-9._:/-]+)\"");
            if (match.Success)
                models.Add(match.Groups[1].Value);
            else if (!string.IsNullOrWhiteSpace(line))
                break;
        }
        return models;
    }
}
