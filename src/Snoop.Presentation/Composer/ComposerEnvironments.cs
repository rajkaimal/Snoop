using System.Text;
using System.Text.Json;
using Snoop.Core;

namespace Snoop.Presentation.Composer;

/// <summary>A variable for <c>{{name}}</c> placeholders.</summary>
public sealed record ComposerVariable(string Name, string Value);

/// <summary>A named set of variables, e.g. "Local" or "Staging".</summary>
public sealed record ComposerEnvironment(string Name, IReadOnlyList<ComposerVariable> Variables);

/// <summary>
/// Composer variables: globals (used everywhere) plus named environments, one of them active. Saved as
/// <c>%APPDATA%\Snoop\environments.json</c> and edited as INI-style text:
/// <code>
/// token = abc          (a global)
/// [Local]
/// baseUrl = http://localhost:5000
/// [Staging]
/// baseUrl = https://staging.example.com
/// </code>
/// </summary>
public sealed record ComposerEnvironments
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static ComposerEnvironments Empty { get; } = new();

    public static string DefaultPath => Path.Combine(SnoopPaths.AppDataDirectory, "environments.json");

    public IReadOnlyList<ComposerVariable> Globals { get; init; } = [];

    public IReadOnlyList<ComposerEnvironment> Environments { get; init; } = [];

    /// <summary>Name of the active environment, or null for none.</summary>
    public string? Active { get; init; }

    /// <summary>Globals overlaid with the active environment's variables (the environment wins).</summary>
    public IReadOnlyDictionary<string, string> ActiveVariables()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in Globals)
        {
            result[variable.Name] = variable.Value;
        }

        var active = Environments.FirstOrDefault(e => string.Equals(e.Name, Active, StringComparison.OrdinalIgnoreCase));
        foreach (var variable in active?.Variables ?? [])
        {
            result[variable.Name] = variable.Value;
        }

        return result;
    }

    /// <summary>The INI-style text for editing.</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        foreach (var variable in Globals)
        {
            builder.Append(variable.Name).Append(" = ").Append(variable.Value).Append('\n');
        }

        foreach (var environment in Environments)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append('[').Append(environment.Name).Append("]\n");
            foreach (var variable in environment.Variables)
            {
                builder.Append(variable.Name).Append(" = ").Append(variable.Value).Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>Reads the INI-style text. Lines starting with # or ; are comments. The active environment is kept when it still exists.</summary>
    public static bool TryParse(string text, string? active, out ComposerEnvironments? result, out string? error)
    {
        result = null;
        var globals = new List<ComposerVariable>();
        var environments = new List<(string Name, List<ComposerVariable> Variables)>();
        List<ComposerVariable> current = globals;
        var lines = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                if (!line.EndsWith(']') || line.Length < 3)
                {
                    error = $"Line {i + 1}: an environment name goes in brackets, like [Staging].";
                    return false;
                }

                var name = line[1..^1].Trim();
                if (name.Length == 0)
                {
                    error = $"Line {i + 1}: the environment needs a name.";
                    return false;
                }

                if (environments.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    error = $"Line {i + 1}: there are two environments called [{name}].";
                    return false;
                }

                current = [];
                environments.Add((name, current));
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                error = $"Line {i + 1}: write variables as name = value.";
                return false;
            }

            var variable = line[..equals].Trim();
            if (!IsName(variable))
            {
                error = $"Line {i + 1}: \"{variable}\" is not a valid variable name (letters, digits, _ . -).";
                return false;
            }

            var value = line[(equals + 1)..].Trim();
            current.RemoveAll(v => v.Name == variable);
            current.Add(new ComposerVariable(variable, value));
        }

        var list = environments.Select(e => new ComposerEnvironment(e.Name, e.Variables)).ToList();
        var keep = list.FirstOrDefault(e => string.Equals(e.Name, active, StringComparison.OrdinalIgnoreCase))?.Name;
        result = new ComposerEnvironments { Globals = globals, Environments = list, Active = keep };
        error = null;
        return true;
    }

    /// <summary>Loads the file; a missing or damaged one gives no variables.</summary>
    public static ComposerEnvironments Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Empty;
            }

            var loaded = JsonSerializer.Deserialize<ComposerEnvironments>(File.ReadAllText(path), JsonOptions);
            if (loaded is null)
            {
                return Empty;
            }

            static IReadOnlyList<ComposerVariable> Clean(IReadOnlyList<ComposerVariable>? variables) =>
                (variables ?? []).Where(v => v is not null && IsName(v.Name)).Select(v => v with { Value = v.Value ?? string.Empty }).ToList();

            return loaded with
            {
                Globals = Clean(loaded.Globals),
                Environments = (loaded.Environments ?? []).Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Name))
                    .Select(e => e with { Variables = Clean(e.Variables) }).ToList(),
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private static bool IsName(string? name) =>
        !string.IsNullOrEmpty(name) && name[0] != '$' && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
}
