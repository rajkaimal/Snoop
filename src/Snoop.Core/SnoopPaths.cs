namespace Snoop.Core;

/// <summary>Well-known folders used by Snoop.</summary>
public static class SnoopPaths
{
    /// <summary>Environment variable that moves <see cref="AppDataDirectory"/> elsewhere (used by the UI tests).</summary>
    public const string DataDirectoryVariable = "SNOOP_DATA_DIR";

    /// <summary>
    /// <c>%APPDATA%\Snoop</c>: settings, rules, saved requests, the root certificate authority. The
    /// <c>SNOOP_DATA_DIR</c> environment variable, when set, points it somewhere else.
    /// </summary>
    public static string AppDataDirectory { get; } =
        Environment.GetEnvironmentVariable(DataDirectoryVariable) is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snoop");

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
