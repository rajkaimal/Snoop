namespace Snoop.Platform.SystemProxy;

/// <summary>Reads and writes the system proxy settings. Abstracted so the controller logic can be tested.</summary>
public interface IProxySettingsApi
{
    ProxySettings Read();

    /// <summary>Applies the settings and tells running applications they changed.</summary>
    void Write(ProxySettings settings);
}

/// <summary>
/// Makes sure Snoop's proxy setting is undone after a hard kill: registers a command that
/// Windows runs once at next logon.
/// </summary>
public interface IStartupRestoreRegistration
{
    void Register();

    void Unregister();
}
