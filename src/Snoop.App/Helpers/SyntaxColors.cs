using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Snoop.Inspection.Formatting;

namespace Snoop.App.Helpers;

/// <summary>Colours for highlighted bodies, close to VS Code's Light+ and Dark+ themes. Plain text keeps the theme's text colour.</summary>
public static class SyntaxColors
{
    private static readonly Dictionary<SyntaxKind, SolidColorBrush> Dark = new()
    {
        [SyntaxKind.Punctuation] = Brush(0x8B, 0x94, 0x9E),
        [SyntaxKind.Key] = Brush(0x9C, 0xDC, 0xFE),
        [SyntaxKind.String] = Brush(0xCE, 0x91, 0x78),
        [SyntaxKind.Number] = Brush(0xB5, 0xCE, 0xA8),
        [SyntaxKind.Literal] = Brush(0x56, 0x9C, 0xD6),
        [SyntaxKind.Tag] = Brush(0x56, 0x9C, 0xD6),
        [SyntaxKind.AttributeName] = Brush(0x9C, 0xDC, 0xFE),
        [SyntaxKind.AttributeValue] = Brush(0xCE, 0x91, 0x78),
        [SyntaxKind.Comment] = Brush(0x6A, 0x99, 0x55),
    };

    private static readonly Dictionary<SyntaxKind, SolidColorBrush> Light = new()
    {
        [SyntaxKind.Punctuation] = Brush(0x6E, 0x77, 0x81),
        [SyntaxKind.Key] = Brush(0x04, 0x51, 0xA5),
        [SyntaxKind.String] = Brush(0xA3, 0x15, 0x15),
        [SyntaxKind.Number] = Brush(0x09, 0x86, 0x58),
        [SyntaxKind.Literal] = Brush(0x00, 0x00, 0xFF),
        [SyntaxKind.Tag] = Brush(0x80, 0x00, 0x00),
        [SyntaxKind.AttributeName] = Brush(0xE5, 0x00, 0x00),
        [SyntaxKind.AttributeValue] = Brush(0x00, 0x00, 0xFF),
        [SyntaxKind.Comment] = Brush(0x00, 0x80, 0x00),
    };

    /// <summary>The brush for <paramref name="kind"/>, or null for plain text.</summary>
    public static Brush? For(SyntaxKind kind, bool dark) =>
        (dark ? Dark : Light).TryGetValue(kind, out var brush) ? brush : null;

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(ColorHelper.FromArgb(255, r, g, b));
}
