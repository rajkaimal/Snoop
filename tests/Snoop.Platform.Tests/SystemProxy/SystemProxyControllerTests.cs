using Snoop.Platform.SystemProxy;

namespace Snoop.Platform.Tests.SystemProxy;

public sealed class SystemProxyControllerTests : IDisposable
{
    private static readonly ProxySettings UserSettings = new(ProxyFlags.Direct | ProxyFlags.AutoDetect, null, "*.corp", "http://wpad/proxy.pac");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "snoop-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeProxyApi _api = new(UserSettings);
    private readonly FakeStartupRestore _startup = new();

    private string BackupFile => Path.Combine(_directory, "proxy-backup.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Enable_saves_a_backup_and_points_at_snoop()
    {
        var controller = NewController();

        controller.Enable("127.0.0.1", 8866);

        Assert.True(controller.IsEnabled);
        Assert.True(File.Exists(BackupFile));
        Assert.True(_api.Current.PointsAt("127.0.0.1", 8866));
        Assert.True(_startup.Registered);
    }

    [Fact]
    public void Disable_restores_the_original_exactly()
    {
        var controller = NewController();
        controller.Enable("127.0.0.1", 8866);

        Assert.True(controller.Disable());

        Assert.Equal(UserSettings, _api.Current);
        Assert.False(controller.IsEnabled);
        Assert.False(File.Exists(BackupFile));
        Assert.False(_startup.Registered);
    }

    [Fact]
    public void Disable_without_enable_does_nothing()
    {
        var controller = NewController();

        Assert.False(controller.Disable());
        Assert.Equal(0, _api.Writes);
    }

    [Fact]
    public void Enabling_twice_keeps_the_original_backup()
    {
        var controller = NewController();
        controller.Enable("127.0.0.1", 8866);

        controller.Enable("127.0.0.1", 9999);
        controller.Disable();

        Assert.Equal(UserSettings, _api.Current);
    }

    [Fact]
    public void Recover_after_a_crash_restores_the_user_setting()
    {
        NewController().Enable("127.0.0.1", 8866);

        // A new process starts; the old one never called Disable.
        var restarted = NewController();
        Assert.True(restarted.RecoverIfNeeded());

        Assert.Equal(UserSettings, _api.Current);
        Assert.False(restarted.RecoverIfNeeded());
    }

    [Fact]
    public void Corrupt_backup_restores_a_direct_connection()
    {
        var controller = NewController();
        controller.Enable("127.0.0.1", 8866);
        File.WriteAllText(BackupFile, "{ not json");

        controller.Disable();

        Assert.Equal(ProxySettings.Direct, _api.Current);
    }

    [Fact]
    public void Failing_write_keeps_the_backup_for_a_later_retry()
    {
        var controller = NewController();
        controller.Enable("127.0.0.1", 8866);
        _api.FailWrites = true;

        Assert.Throws<IOException>(() => controller.Disable());

        Assert.True(File.Exists(BackupFile));
        _api.FailWrites = false;
        Assert.True(controller.Disable());
        Assert.Equal(UserSettings, _api.Current);
    }

    [Fact]
    public void Startup_registration_failure_does_not_block_enable()
    {
        _startup.Fail = true;
        var controller = NewController();

        controller.Enable("127.0.0.1", 8866);

        Assert.True(_api.Current.PointsAt("127.0.0.1", 8866));
    }

    private SystemProxyController NewController() => new(_api, BackupFile, _startup);

    private sealed class FakeProxyApi(ProxySettings initial) : IProxySettingsApi
    {
        public ProxySettings Current { get; private set; } = initial;

        public int Writes { get; private set; }

        public bool FailWrites { get; set; }

        public ProxySettings Read() => Current;

        public void Write(ProxySettings settings)
        {
            if (FailWrites)
            {
                throw new IOException("simulated failure");
            }

            Writes++;
            Current = settings;
        }
    }

    private sealed class FakeStartupRestore : IStartupRestoreRegistration
    {
        public bool Registered { get; private set; }

        public bool Fail { get; set; }

        public void Register()
        {
            if (Fail)
            {
                throw new UnauthorizedAccessException("simulated");
            }

            Registered = true;
        }

        public void Unregister() => Registered = false;
    }
}
