using System.Text.Json;
using System.Text.Json.Serialization;
using Snoop.Core;
using Snoop.Proxy.Rules;

namespace Snoop.Hosting.Rules;

/// <summary>Keeps traffic rules in <c>%APPDATA%\Snoop\rules.json</c>.</summary>
public static class RulesFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(SnoopPaths.AppDataDirectory, "rules.json");

    /// <summary>Loads rules. A missing file gives no rules.</summary>
    /// <exception cref="InvalidDataException">The file exists but cannot be read as rules.</exception>
    public static IReadOnlyList<TrafficRule> Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var document = JsonSerializer.Deserialize<RulesDocument>(File.ReadAllText(path), JsonOptions);
            return document?.Rules?.Where(r => r is not null).Select(Sanitize).ToList() ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The rules file {path} is damaged: {ex.Message}", ex);
        }
    }

    public static void Save(IEnumerable<TrafficRule> rules, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        path ??= DefaultPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new RulesDocument { Rules = [.. rules] }, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private static TrafficRule Sanitize(TrafficRule rule) => rule with
    {
        Name = rule.Name ?? string.Empty,
        Pattern = rule.Pattern ?? string.Empty,
        Method = rule.Method ?? string.Empty,
        Actions = (rule.Actions ?? []).Where(a => a is not null).Select(a => a with { Name = a.Name ?? string.Empty, Value = a.Value ?? string.Empty }).ToList(),
    };

    private sealed class RulesDocument
    {
        public int Version { get; set; } = 1;

        public List<TrafficRule>? Rules { get; set; }
    }
}
