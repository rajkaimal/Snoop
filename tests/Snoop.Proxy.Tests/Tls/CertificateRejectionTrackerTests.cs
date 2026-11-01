using Snoop.Proxy.Tls;
using Snoop.Proxy.Util;

namespace Snoop.Proxy.Tests.Tls;

public class CertificateRejectionTrackerTests
{
    [Fact]
    public void Host_is_skipped_once_the_threshold_is_reached()
    {
        var tracker = new CertificateRejectionTracker(threshold: 2);

        Assert.False(tracker.RecordRejection("claude.ai"));
        Assert.False(tracker.IsSkipped("claude.ai"));
        Assert.True(tracker.RecordRejection("CLAUDE.ai."));

        Assert.True(tracker.IsSkipped("claude.ai"));
        Assert.Contains("claude.ai", tracker.SkippedHosts.Keys);
    }

    [Fact]
    public void Newly_skipped_is_reported_only_once()
    {
        var tracker = new CertificateRejectionTracker(threshold: 1);

        Assert.True(tracker.RecordRejection("a.example"));
        Assert.False(tracker.RecordRejection("a.example"));
    }

    [Fact]
    public void Manual_skip_works_even_when_auto_skip_is_off()
    {
        var tracker = new CertificateRejectionTracker(threshold: 0);

        Assert.True(tracker.Skip("Example.com"));
        Assert.False(tracker.Skip("example.com"));
        Assert.True(tracker.IsSkipped("example.com"));
    }

    [Fact]
    public void Success_resets_the_count()
    {
        var tracker = new CertificateRejectionTracker(threshold: 2);

        tracker.RecordRejection("example.com");
        tracker.RecordSuccess("example.com");
        tracker.RecordRejection("example.com");

        Assert.False(tracker.IsSkipped("example.com"));
    }

    [Fact]
    public void Hosts_are_counted_separately()
    {
        var tracker = new CertificateRejectionTracker(threshold: 2);

        tracker.RecordRejection("a.example");
        tracker.RecordRejection("b.example");

        Assert.False(tracker.IsSkipped("a.example"));
        Assert.False(tracker.IsSkipped("b.example"));
    }

    [Fact]
    public void Zero_threshold_never_skips()
    {
        var tracker = new CertificateRejectionTracker(threshold: 0);

        for (var i = 0; i < 10; i++)
        {
            Assert.False(tracker.RecordRejection("example.com"));
        }

        Assert.False(tracker.IsSkipped("example.com"));
    }

    [Theory]
    [InlineData("claude.ai")]
    [InlineData("api.claude.ai")]
    [InlineData("statsig.anthropic.com")]
    [InlineData("drivefrontend-pa.googleapis.com")]
    [InlineData("download.windowsupdate.com")]
    public void Built_in_list_covers_known_pinned_apps(string host)
    {
        Assert.True(new HostMatcher(ProxyOptions.BuiltInDoNotDecryptHosts).IsMatch(host));
    }

    [Theory]
    [InlineData("www.google.com")]
    [InlineData("mail.google.com")]
    [InlineData("example.com")]
    public void Built_in_list_leaves_ordinary_sites_alone(string host)
    {
        Assert.False(new HostMatcher(ProxyOptions.BuiltInDoNotDecryptHosts).IsMatch(host));
    }
}
