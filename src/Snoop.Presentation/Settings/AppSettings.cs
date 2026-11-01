using System.Text.Json;
using Snoop.Core;
using Snoop.Hosting;
using Snoop.Proxy;

namespace Snoop.Presentation.Settings;

/// <summary>User preferences, saved as JSON in <c>%APPDATA%\Snoop\settings.json</c>.</summary>
public sealed record AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public int Port { get; init; } = ProxyOptions.DefaultPort;

    public bool CaptureOnStartup { get; init; } = true;

    /// <summary>Make Snoop the system proxy whenever capture starts.</summary>
    public bool UseSystemProxy { get; init; } = true;

    public bool DecryptHttps { get; init; } = true;

    public bool CaptureLoopback { get; init; } = true;

    public bool UseBuiltInSkips { get; init; } = true;

    public bool AutoSkip { get; init; } = true;

    public IReadOnlyList<string> DoNotDecryptHosts { get; init; } = [];

    public bool AutoScroll { get; init; } = true;

    public bool HideTunnels { get; init; } = true;

    /// <summary>Let the search box match text in request and response bodies.</summary>
    public bool SearchBodies { get; init; } = true;

    /// <summary>Listen on all network interfaces so phones and other PCs can use Snoop.</summary>
    public bool AllowRemote { get; init; }

    /// <summary>Remove zstd from Accept-Encoding so response bodies stay readable.</summary>
    public bool AvoidZstd { get; init; } = true;

    /// <summary>Session grid columns: width and visibility, by column name. Missing columns use defaults.</summary>
    public IReadOnlyList<ColumnSetting> Columns { get; init; } = [];

    /// <summary>Column the grid is sorted by (null: arrival order).</summary>
    public string? SortColumn { get; init; }

    public bool SortDescending { get; init; }

    /// <summary>Named filters the user saved.</summary>
    public IReadOnlyList<SavedFilter> SavedFilters { get; init; } = [];

    public static string DefaultPath => Path.Combine(SnoopPaths.AppDataDirectory, "settings.json");

    public CaptureSettings ToCaptureSettings() => new()
    {
        Port = Port,
        DecryptHttps = DecryptHttps,
        SetSystemProxy = UseSystemProxy,
        CaptureLoopback = CaptureLoopback,
        DoNotDecryptHosts = DoNotDecryptHosts,
        UseBuiltInSkips = UseBuiltInSkips,
        AutoSkip = AutoSkip,
        AllowRemote = AllowRemote,
        AvoidZstd = AvoidZstd,
    };

    /// <summary>Loads settings; a missing or unreadable file gives the defaults.</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>A session grid column's saved layout.</summary>
public sealed record ColumnSetting(string Name, double Width, bool Visible);

/// <summary>A filter the user saved under a name. <paramref name="Host"/> and <paramref name="ProcessName"/> limit it to one host or app.</summary>
public sealed record SavedFilter(string Name, string SearchText, bool HideTunnels, bool HideStatic, bool OnlyErrors, string Host = "", string ProcessName = "");
