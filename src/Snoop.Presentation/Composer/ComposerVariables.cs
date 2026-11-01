using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Snoop.Presentation.Composer;

/// <summary>
/// Replaces <c>{{name}}</c> placeholders with variable values. A few built-in names start with $:
/// <c>{{$guid}}</c>, <c>{{$timestamp}}</c> (Unix seconds), <c>{{$isoTimestamp}}</c> and <c>{{$randomInt}}</c> (0-999).
/// </summary>
public static partial class ComposerVariables
{
    public static IReadOnlyList<string> BuiltInNames { get; } = ["$guid", "$timestamp", "$isoTimestamp", "$randomInt"];

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.$-]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    /// <summary>True when <paramref name="text"/> has any placeholder.</summary>
    public static bool HasPlaceholders(string? text) => text is not null && Placeholder().IsMatch(text);

    /// <summary>
    /// Replaces every known placeholder in <paramref name="text"/>. Unknown names are left as they are and
    /// added to <paramref name="missing"/>. Values are not resolved again (a value may contain braces).
    /// </summary>
    public static string Resolve(string text, IReadOnlyDictionary<string, string> variables, ISet<string> missing, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(missing);
        if (!text.Contains("{{", StringComparison.Ordinal))
        {
            return text;
        }

        var time = now ?? DateTimeOffset.UtcNow;
        return Placeholder().Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            if (variables.TryGetValue(name, out var value))
            {
                return value;
            }

            switch (name)
            {
                case "$guid":
                    return Guid.NewGuid().ToString();
                case "$timestamp":
                    return time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                case "$isoTimestamp":
                    return time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
                case "$randomInt":
                    return RandomNumberGenerator.GetInt32(1000).ToString(CultureInfo.InvariantCulture);
                default:
                    missing.Add(name);
                    return match.Value;
            }
        });
    }
}
