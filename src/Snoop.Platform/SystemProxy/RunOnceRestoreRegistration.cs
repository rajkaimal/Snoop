using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Snoop.Platform.SystemProxy;

/// <summary>
/// Registers <c>"snoop.exe" restore-proxy</c> under HKCU\...\RunOnce while Snoop is the
/// system proxy, so that if Snoop is killed the setting is undone at next logon.
/// Removed again on a clean shutdown.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RunOnceRestoreRegistration(string executablePath, string arguments = "restore-proxy") : IStartupRestoreRegistration
{
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ValueName = "SnoopRestoreProxy";

    public void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunOnceKey, writable: true);
        key.SetValue(ValueName, $"\"{executablePath}\" {arguments}", RegistryValueKind.String);
    }

    public void Unregister()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunOnceKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
