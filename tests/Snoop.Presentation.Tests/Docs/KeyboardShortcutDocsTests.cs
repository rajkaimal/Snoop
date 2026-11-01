using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Snoop.Presentation.Tests.Docs;

/// <summary>
/// Keeps the README's "Keyboard shortcuts" tables and the app's real shortcuts in step: every shortcut
/// the app declares must be documented, and every documented one must exist (declared in XAML, handled
/// in code, or built into the WinUI controls). Runs on any OS; it reads the source files.
/// </summary>
public sealed partial class KeyboardShortcutDocsTests
{
    /// <summary>Shortcuts handled in code-behind key handlers, with the text that proves the handler is there.</summary>
    private static readonly (string Keys, string File, string Evidence)[] HandledInCode =
    [
        ("Delete", "src/Snoop.App/MainWindow.xaml.cs", "VirtualKey.Delete"),
        ("Ctrl+C", "src/Snoop.App/MainWindow.xaml.cs", "VirtualKey.C"),
        ("Ctrl+Shift+C", "src/Snoop.App/MainWindow.xaml.cs", "VirtualKey.Shift"),
        ("Enter", "src/Snoop.App/MainWindow.xaml.cs", "VirtualKey.Enter"),
        ("Shift+Enter", "src/Snoop.App/MainWindow.xaml.cs", "VirtualKey.Shift"),
        ("Esc", "src/Snoop.App/MainWindow.xaml.cs", "VirtualKey.Escape"),
        ("Ctrl+Enter", "src/Snoop.App/Views/ComposerView.xaml.cs", "VirtualKey.Enter"),
        ("Double-click", "src/Snoop.App/MainWindow.xaml", "DoubleTapped=\"OnSessionDoubleTapped\""),
    ];

    /// <summary>Provided by the WinUI list and menu controls themselves.</summary>
    private static readonly HashSet<string> BuiltIn = ["↑", "↓", "Home", "End", "Ctrl+click", "Shift+click", "Ctrl+A", "Shift+F10", "Menu key"];

    private static string Root([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", ".."));

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative));

    [GeneratedRegex(@"<KeyboardAccelerator\b([^>]*)/>")]
    private static partial Regex AcceleratorElement();

    [GeneratedRegex(@"\b(Modifiers|Key)=""([^""]*)""")]
    private static partial Regex Attribute();

    [GeneratedRegex(@"KeyboardAcceleratorTextOverride=""([^""]*)""")]
    private static partial Regex MenuShortcut();

    /// <summary>Shortcuts declared as KeyboardAccelerators in the app's XAML, e.g. "Ctrl+Shift+F3".</summary>
    private static HashSet<string> DeclaredInXaml()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src", "Snoop.App"), "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match element in AcceleratorElement().Matches(File.ReadAllText(file)))
            {
                var attributes = Attribute().Matches(element.Groups[1].Value).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
                var modifiers = attributes.GetValueOrDefault("Modifiers", string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(m => m switch { "Control" => "Ctrl", "Menu" => "Alt", _ => m })
                    .OrderBy(m => m switch { "Ctrl" => 0, "Alt" => 1, "Shift" => 2, _ => 3 });
                var key = attributes["Key"] switch
                {
                    var k when k.StartsWith("Number", StringComparison.Ordinal) => k["Number".Length..],
                    "Left" => "←",
                    "Right" => "→",
                    "Up" => "↑",
                    "Down" => "↓",
                    "Escape" => "Esc",
                    var k => k,
                };
                result.Add(string.Join('+', modifiers.Append(key)));
            }
        }

        return result;
    }

    /// <summary>Every key combination in the README's "Keyboard shortcuts" tables.</summary>
    private static HashSet<string> Documented()
    {
        var readme = Read("README.md").Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = readme.IndexOf("\n## Keyboard shortcuts\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "README.md has no \"## Keyboard shortcuts\" section.");
        var end = readme.IndexOf("\n## ", start + 5, StringComparison.Ordinal);
        var section = readme[start..(end < 0 ? readme.Length : end)];

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in section.Split('\n'))
        {
            if (!line.StartsWith("| ", StringComparison.Ordinal) || line.StartsWith("| ---", StringComparison.Ordinal) || line.StartsWith("| Keys ", StringComparison.Ordinal))
            {
                continue;
            }

            var cell = line.Split('|', StringSplitOptions.TrimEntries)[1];
            foreach (var part in cell.Split([" / ", ", ", " or "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var range = Regex.Match(part, @"^(.*\+)(\d) … \1(\d)$");
                if (range.Success)
                {
                    for (var n = int.Parse(range.Groups[2].Value); n <= int.Parse(range.Groups[3].Value); n++)
                    {
                        result.Add(range.Groups[1].Value + n);
                    }

                    continue;
                }

                result.Add(part.StartsWith("the ", StringComparison.Ordinal) ? part[4..] : part);
            }
        }

        return result;
    }

    [Fact]
    public void Every_declared_shortcut_is_in_the_readme()
    {
        var documented = Documented();
        var missing = DeclaredInXaml().Concat(HandledInCode.Select(h => h.Keys)).Where(k => !documented.Contains(k)).Distinct().ToList();
        Assert.True(missing.Count == 0, "Not in the README's Keyboard shortcuts tables: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_documented_shortcut_exists()
    {
        var known = DeclaredInXaml();
        known.UnionWith(HandledInCode.Select(h => h.Keys));
        known.UnionWith(BuiltIn);
        var unknown = Documented().Where(k => !known.Contains(k)).ToList();
        Assert.True(unknown.Count == 0, "Documented but not implemented: " + string.Join(", ", unknown));
    }

    [Fact]
    public void Shortcuts_handled_in_code_are_still_there()
    {
        foreach (var (keys, file, evidence) in HandledInCode)
        {
            Assert.True(Read(file).Contains(evidence, StringComparison.Ordinal), $"{keys}: expected \"{evidence}\" in {file}.");
        }
    }

    [Fact]
    public void Shortcuts_shown_in_menus_are_real()
    {
        var known = DeclaredInXaml();
        known.UnionWith(HandledInCode.Select(h => h.Keys));
        var shown = MenuShortcut().Matches(Read("src/Snoop.App/MainWindow.xaml")).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(shown);
        Assert.All(shown, s => Assert.Contains(s, known));
    }

    [Fact]
    public void Readme_tables_were_found()
    {
        var documented = Documented();
        Assert.Contains("Ctrl+F", documented);
        Assert.Contains("Ctrl+6", documented);
        Assert.Contains("Alt+←", documented);
        Assert.Contains("Menu key", documented);
    }
}
