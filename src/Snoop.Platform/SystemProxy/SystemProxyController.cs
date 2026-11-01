using System.Text.Json;
using System.Text.Json.Serialization;
using Snoop.Core.Diagnostics;

namespace Snoop.Platform.SystemProxy;

/// <summary>
/// Points the system proxy at Snoop and, above all, puts it back afterwards.
/// <para>
/// Before changing anything the user's settings are saved to a backup file. The backup is
/// deleted only after a successful restore, so its existence means "Snoop may still be the
/// system proxy". <see cref="RecoverIfNeeded"/> uses that at startup to repair the setting
/// after a crash, and a RunOnce entry does the same at next logon after a hard kill.
/// </para>
/// </summary>
public sealed class SystemProxyController
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IProxySettingsApi _api;
    private readonly string _backupFile;
    private readonly IStartupRestoreRegistration? _startupRestore;
    private readonly ISnoopLogger _logger;
    private readonly Lock _gate = new();

    public SystemProxyController(IProxySettingsApi api, string backupFile, IStartupRestoreRegistration? startupRestore = null, ISnoopLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentException.ThrowIfNullOrEmpty(backupFile);
        _api = api;
        _backupFile = backupFile;
        _startupRestore = startupRestore;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>True when a backup exists, i.e. Snoop has changed the setting and not yet restored it.</summary>
    public bool IsEnabled
    {
        get { lock (_gate) { return File.Exists(_backupFile); } }
    }

    /// <summary>Routes this user's traffic through the proxy at <paramref name="host"/>:<paramref name="port"/>.</summary>
    public void Enable(string host, int port, bool captureLoopback = true)
    {
        lock (_gate)
        {
            var current = _api.Read();
            if (File.Exists(_backupFile))
            {
                // A backup from an earlier run is the user's real setting; never overwrite it with ours.
                _logger.Info("Keeping the existing proxy backup from an earlier run.");
            }
            else
            {
                SaveBackup(current);
            }

            var original = LoadBackup() ?? current;
            TryRun(() => _startupRestore?.Register(), "register the logon restore");
            _api.Write(ProxySettings.ForProxy(host, port, captureLoopback, original));
            _logger.Info($"System proxy set to {host}:{port}.");
        }
    }

    /// <summary>Restores the user's original setting. Does nothing when Snoop is not the system proxy.</summary>
    /// <returns>True when a setting was restored.</returns>
    public bool Disable()
    {
        lock (_gate)
        {
            var backup = LoadBackup();
            if (backup is null)
            {
                return false;
            }

            _api.Write(backup);
            File.Delete(_backupFile);
            TryRun(() => _startupRestore?.Unregister(), "remove the logon restore");
            _logger.Info("System proxy restored.");
            return true;
        }
    }

    /// <summary>
    /// Call at startup: if an earlier run did not shut down cleanly, puts the user's setting back.
    /// </summary>
    /// <returns>True when a stale setting was repaired.</returns>
    public bool RecoverIfNeeded()
    {
        if (!IsEnabled)
        {
            return false;
        }

        _logger.Warn("Snoop did not shut down cleanly last time; restoring the system proxy setting.");
        return Disable();
    }

    private void SaveBackup(ProxySettings settings)
    {
        var directory = Path.GetDirectoryName(_backupFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _backupFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, _backupFile, overwrite: true);
    }

    private ProxySettings? LoadBackup()
    {
        if (!File.Exists(_backupFile))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProxySettings>(File.ReadAllText(_backupFile), JsonOptions);
        }
        catch (JsonException ex)
        {
            // A corrupt backup must not leave the user stuck behind a dead proxy: fall back to "no proxy".
            _logger.Error("The proxy backup file is corrupt; restoring a direct connection instead.", ex);
            return ProxySettings.Direct;
        }
    }

    private void TryRun(Action action, string what)
    {
        try
        {
            action();
        }
#pragma warning disable CA1031 // The logon restore is a safety net; failing to set it must not block the main action.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.Warn($"Could not {what}.", ex);
        }
    }
}
