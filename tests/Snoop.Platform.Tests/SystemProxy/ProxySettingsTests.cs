using Snoop.Platform.SystemProxy;

namespace Snoop.Platform.Tests.SystemProxy;

public class ProxySettingsTests
{
    [Fact]
    public void ForProxy_routes_http_and_https_and_disables_auto_config()
    {
        var previous = new ProxySettings(ProxyFlags.Direct | ProxyFlags.AutoDetect | ProxyFlags.AutoProxyUrl, null, null, "http://wpad/proxy.pac");

        var settings = ProxySettings.ForProxy("127.0.0.1", 8866, captureLoopback: false, previous);

        Assert.Equal(ProxyFlags.Direct | ProxyFlags.Proxy, settings.Flags);
        Assert.Equal("http=127.0.0.1:8866;https=127.0.0.1:8866", settings.ProxyServer);
        Assert.Null(settings.AutoConfigUrl);
        Assert.Null(settings.BypassList);
        Assert.True(settings.PointsAt("127.0.0.1", 8866));
    }

    [Fact]
    public void Capturing_loopback_adds_the_token_and_drops_local_names()
    {
        var previous = new ProxySettings(ProxyFlags.Direct, null, "*.corp;<local>", null);

        var settings = ProxySettings.ForProxy("127.0.0.1", 8866, captureLoopback: true, previous);

        Assert.Equal("*.corp;<-loopback>", settings.BypassList);
    }

    [Fact]
    public void Not_capturing_loopback_keeps_the_user_bypass_list()
    {
        var previous = new ProxySettings(ProxyFlags.Direct, null, "*.corp;<local>;<-loopback>", null);

        var settings = ProxySettings.ForProxy("127.0.0.1", 8866, captureLoopback: false, previous);

        Assert.Equal("*.corp;<local>", settings.BypassList);
    }

    [Fact]
    public void PointsAt_is_false_for_direct_settings()
    {
        Assert.False(ProxySettings.Direct.PointsAt("127.0.0.1", 8866));
    }
}
