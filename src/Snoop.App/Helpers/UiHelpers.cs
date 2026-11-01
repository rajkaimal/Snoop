using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Snoop.Presentation.ViewModels;

namespace Snoop.App.Helpers;

/// <summary>Small conversion functions used from XAML with x:Bind function bindings.</summary>
public static class UiHelpers
{
    private static readonly SolidColorBrush Fallback = new(ColorHelper.FromArgb(255, 0x80, 0x80, 0x80));
    private static readonly SolidColorBrush Redirect = new(ColorHelper.FromArgb(255, 0x3B, 0x82, 0xF6));
    private static readonly SolidColorBrush ClientError = new(ColorHelper.FromArgb(255, 0xD9, 0x7A, 0x06));
    private static readonly SolidColorBrush ServerError = new(ColorHelper.FromArgb(255, 0xDC, 0x26, 0x26));
    private static readonly SolidColorBrush Failed = new(ColorHelper.FromArgb(255, 0xB9, 0x1C, 0x1C));
    private static readonly SolidColorBrush Muted = new(ColorHelper.FromArgb(255, 0x8A, 0x8A, 0x8A));

    /// <summary>Colour for the Result column. Successful results use the theme's normal text colour.</summary>
    public static Brush CategoryBrush(StatusCategory category) =>
        category switch
        {
            StatusCategory.Redirect => Redirect,
            StatusCategory.ClientError => ClientError,
            StatusCategory.ServerError => ServerError,
            StatusCategory.Failed => Failed,
            StatusCategory.Tunnel or StatusCategory.Pending => Muted,
            _ => ThemeTextBrush(),
        };

    /// <summary>The theme's primary text brush (never null: a null Foreground hides the text).</summary>
    private static Brush ThemeTextBrush() =>
        Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("TextFillColorPrimaryBrush", out var brush) && brush is Brush themed
            ? themed
            : Fallback;

    /// <summary>Opacity for whole rows: tunnels and pending requests are dimmed.</summary>
    public static double RowOpacity(StatusCategory category) =>
        category is StatusCategory.Tunnel or StatusCategory.Pending ? 0.65 : 1.0;

    private static readonly SolidColorBrush AddedLine = new(ColorHelper.FromArgb(48, 0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush RemovedLine = new(ColorHelper.FromArgb(48, 0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush NoBackground = new(Colors.Transparent);
    private static readonly SolidColorBrush Sent = new(ColorHelper.FromArgb(255, 0x0E, 0xA5, 0xE9));
    private static readonly SolidColorBrush Received = new(ColorHelper.FromArgb(255, 0x22, 0xC5, 0x5E));

    private static readonly SolidColorBrush Capturing = new(ColorHelper.FromArgb(255, 0x22, 0xC5, 0x5E));

    /// <summary>Record (start) or stop glyph for the capture button.</summary>
    public static string CaptureGlyph(bool capturing) => capturing ? "\uE71A" : "\uE7C8";

    /// <summary>Green dot while capturing, grey otherwise.</summary>
    public static Brush CaptureBrush(bool capturing) => capturing ? Capturing : Muted;

    /// <summary>A fixed width in pixels (0 hides a column).</summary>
    public static GridLength Px(double width) => new(Math.Max(0, width));

    /// <summary>A proportional width (timeline bars).</summary>
    public static GridLength Star(double weight) => new(Math.Max(0, weight), GridUnitType.Star);

    /// <summary>Background of a Compare line: green added, red removed.</summary>
    public static Brush DiffBrush(Snoop.Inspection.Comparing.DiffKind kind) => kind switch
    {
        Snoop.Inspection.Comparing.DiffKind.Added => AddedLine,
        Snoop.Inspection.Comparing.DiffKind.Removed => RemovedLine,
        _ => NoBackground,
    };

    /// <summary>Colour of a WebSocket direction arrow.</summary>
    public static Brush DirectionBrush(bool fromClient) => fromClient ? Sent : Received;

    /// <summary>Red for errors, the secondary text colour otherwise.</summary>
    public static Brush MessageBrush(bool isError) =>
        isError ? ServerError : ThemeBrush("TextFillColorSecondaryBrush");

    private static Brush ThemeBrush(string key) =>
        Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var brush) && brush is Brush themed
            ? themed
            : Fallback;

    public static InfoBarSeverity Severity(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;

    /// <summary>Builds an image from captured bytes. Null when there is nothing to show.</summary>
    public static ImageSource? ToImage(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(bytes, writable: false);
            bitmap.SetSource(stream.AsRandomAccessStream());
            return bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public static Microsoft.UI.Xaml.Visibility VisibleWhen(bool value) =>
        value ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public static Microsoft.UI.Xaml.Visibility VisibleWhenNot(bool value) => VisibleWhen(!value);

    /// <summary>Visible for counts above zero (rail badges).</summary>
    public static Microsoft.UI.Xaml.Visibility VisibleWhenPositive(int value) => VisibleWhen(value > 0);
}
