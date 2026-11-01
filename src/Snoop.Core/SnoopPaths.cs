namespace Snoop.Core;

/// <summary>Well-known folders used by Snoop.</summary>
public static class SnoopPaths
{
    /// <summary><c>%APPDATA%\Snoop</c>: settings, rules, the root certificate authority.</summary>
    public static string AppDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snoop");

    /// <summary><c>%TEMP%\Snoop</c>: spilled bodies for the running capture.</summary>
    public static string DefaultTempDirectory { get; } = Path.Combine(Path.GetTempPath(), "Snoop");

    /// <summary>Where the original system proxy settings are saved while Snoop is the proxy.</summary>
    public static string ProxyBackupFile => Path.Combine(AppDataDirectory, "proxy-backup.json");

    /// <summary>Folder that holds the root certificate authority files.</summary>
    public static string CertificateDirectory => Path.Combine(AppDataDirectory, "certificates");

    /// <summary>
    /// Deletes leftover spilled bodies from earlier runs. Safe to call at startup;
    /// files still in use are skipped.
    /// </summary>
    public static int SweepTempDirectory(string? directory = null)
    {
        directory ??= DefaultTempDirectory;
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "body-*.bin"))
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return deleted;
    }
}
