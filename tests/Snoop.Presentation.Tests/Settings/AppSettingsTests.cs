using Snoop.Presentation.Settings;

namespace Snoop.Presentation.Tests.Settings;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "snoop-tests", Guid.NewGuid().ToString("N"), "settings.json");

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Round_trips_through_a_file()
    {
        var settings = new AppSettings { Port = 9999, DecryptHttps = false, DoNotDecryptHosts = ["a.example", "*.b.example"] };

        settings.Save(_path);
        var loaded = AppSettings.Load(_path);

        Assert.Equal(9999, loaded.Port);
        Assert.False(loaded.DecryptHttps);
        Assert.Equal(new[] { "a.example", "*.b.example" }, loaded.DoNotDecryptHosts);
    }

    [Fact]
    public void Missing_or_corrupt_file_gives_defaults()
    {
        Assert.Equal(8866, AppSettings.Load(_path).Port);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ not json");

        Assert.True(AppSettings.Load(_path).DecryptHttps);
    }

    [Fact]
    public void Maps_to_capture_settings()
    {
        var capture = new AppSettings { Port = 1234, UseSystemProxy = true, CaptureLoopback = false, AutoSkip = false }.ToCaptureSettings();

        Assert.Equal(1234, capture.Port);
        Assert.True(capture.SetSystemProxy);
        Assert.False(capture.CaptureLoopback);
        Assert.False(capture.AutoSkip);
    }
}
