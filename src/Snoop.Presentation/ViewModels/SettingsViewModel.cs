using System.Globalization;
using System.Net;
using Snoop.Core;
using Snoop.Presentation.Editing;
using Snoop.Presentation.Mvvm;
using Snoop.Presentation.Settings;
using Snoop.Proxy;

namespace Snoop.Presentation.ViewModels;

/// <summary>
/// The Settings tab. Holds an editable copy of the settings; <see cref="TryBuild"/> turns it back into
/// <see cref="AppSettings"/> when the user applies it.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private AppSettings _loaded = new();
    private string _portText = string.Empty;
    private bool _captureOnStartup;
    private bool _useSystemProxy;
    private bool _captureLoopback;
    private bool _decryptHttps;
    private bool _useBuiltInSkips;
    private bool _autoSkip;
    private bool _allowRemote;
    private bool _avoidZstd;
    private string _skipHostsText = string.Empty;
    private string? _error;
    private bool _isDirty;

    public string PortText
    {
        get => _portText;
        set => Edit(ref _portText, value ?? string.Empty);
    }

    public bool CaptureOnStartup
    {
        get => _captureOnStartup;
        set => Edit(ref _captureOnStartup, value);
    }

    public bool UseSystemProxy
    {
        get => _useSystemProxy;
        set => Edit(ref _useSystemProxy, value);
    }

    public bool CaptureLoopback
    {
        get => _captureLoopback;
        set => Edit(ref _captureLoopback, value);
    }

    public bool DecryptHttps
    {
        get => _decryptHttps;
        set => Edit(ref _decryptHttps, value);
    }

    public bool UseBuiltInSkips
    {
        get => _useBuiltInSkips;
        set => Edit(ref _useBuiltInSkips, value);
    }

    public bool AutoSkip
    {
        get => _autoSkip;
        set => Edit(ref _autoSkip, value);
    }

    public bool AllowRemote
    {
        get => _allowRemote;
        set => Edit(ref _allowRemote, value);
    }

    public bool AvoidZstd
    {
        get => _avoidZstd;
        set => Edit(ref _avoidZstd, value);
    }

    /// <summary>Hosts never decrypted, one per line; *.example.com allowed.</summary>
    public string SkipHostsText
    {
        get => _skipHostsText;
        set => Edit(ref _skipHostsText, MessageText.NormalizeNewlines(value ?? string.Empty));
    }

    /// <summary>The built-in skip list, for reference.</summary>
    public static string BuiltInSkipsText { get; } = string.Join(", ", ProxyOptions.BuiltInDoNotDecryptHosts);

    public static string DataFolder => SnoopPaths.AppDataDirectory;

    /// <summary>True when there are edits not applied yet.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(ErrorText));
            }
        }
    }

    public bool HasError => _error is not null;

    public string ErrorText => _error ?? string.Empty;

    /// <summary>Fills the page from <paramref name="settings"/>, discarding edits.</summary>
    public void Load(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _loaded = settings;
        _portText = settings.Port.ToString(CultureInfo.InvariantCulture);
        _captureOnStartup = settings.CaptureOnStartup;
        _useSystemProxy = settings.UseSystemProxy;
        _captureLoopback = settings.CaptureLoopback;
        _decryptHttps = settings.DecryptHttps;
        _useBuiltInSkips = settings.UseBuiltInSkips;
        _autoSkip = settings.AutoSkip;
        _allowRemote = settings.AllowRemote;
        _avoidZstd = settings.AvoidZstd;
        _skipHostsText = string.Join("\n", settings.DoNotDecryptHosts);
        Error = null;
        IsDirty = false;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>The edited settings (everything not on this page is kept), or null with <see cref="Error"/> set.</summary>
    public AppSettings? TryBuild()
    {
        if (!int.TryParse(_portText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port > IPEndPoint.MaxPort)
        {
            Error = "The port must be a number from 1 to 65535 (0 picks any free port).";
            return null;
        }

        var hosts = MessageText.SplitLines(_skipHostsText)
            .SelectMany(line => line.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var bad = hosts.FirstOrDefault(h => !IsHostPattern(h));
        if (bad is not null)
        {
            Error = $"'{bad}' is not a host name or pattern such as *.example.com.";
            return null;
        }

        Error = null;
        return _loaded with
        {
            Port = port,
            CaptureOnStartup = _captureOnStartup,
            UseSystemProxy = _useSystemProxy,
            CaptureLoopback = _captureLoopback,
            DecryptHttps = _decryptHttps,
            UseBuiltInSkips = _useBuiltInSkips,
            AutoSkip = _autoSkip,
            AllowRemote = _allowRemote,
            AvoidZstd = _avoidZstd,
            DoNotDecryptHosts = hosts,
        };
    }

    /// <summary>True when going from <paramref name="before"/> to <paramref name="after"/> needs the capture restarted.</summary>
    public static bool NeedsRestart(AppSettings before, AppSettings after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return before.Port != after.Port
               || before.DecryptHttps != after.DecryptHttps
               || before.CaptureLoopback != after.CaptureLoopback
               || before.UseBuiltInSkips != after.UseBuiltInSkips
               || before.AutoSkip != after.AutoSkip
               || before.AllowRemote != after.AllowRemote
               || before.AvoidZstd != after.AvoidZstd
               || !before.DoNotDecryptHosts.SequenceEqual(after.DoNotDecryptHosts, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsHostPattern(string host)
    {
        var name = host.StartsWith("*.", StringComparison.Ordinal) ? host[2..] : host;
        return name.Length > 0 && Uri.CheckHostName(name) != UriHostNameType.Unknown;
    }

    private void Edit<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (SetProperty(ref field, value, name))
        {
            IsDirty = true;
        }
    }
}
