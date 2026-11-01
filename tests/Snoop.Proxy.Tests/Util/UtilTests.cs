using Snoop.Proxy.Util;

namespace Snoop.Proxy.Tests.Util;

public class HostMatcherTests
{
    [Theory]
    [InlineData("example.com", true)]
    [InlineData("EXAMPLE.com.", true)]
    [InlineData("www.example.com", false)]
    [InlineData("api.pinned.net", true)]
    [InlineData("a.b.pinned.net", true)]
    [InlineData("pinned.net", false)]
    [InlineData("notpinned.net", false)]
    [InlineData("", false)]
    public void Matches_exact_and_wildcard_patterns(string host, bool expected)
    {
        var matcher = new HostMatcher(["example.com", "*.pinned.net", "  ", "."]);

        Assert.Equal(expected, matcher.IsMatch(host));
    }

    [Fact]
    public void Star_matches_everything()
    {
        Assert.True(new HostMatcher(["*"]).IsMatch("anything.at.all"));
    }

    [Fact]
    public void Empty_matches_nothing()
    {
        Assert.False(HostMatcher.Empty.IsMatch("example.com"));
    }
}

public class LruCacheTests
{
    [Fact]
    public void Evicts_the_least_recently_used()
    {
        var cache = new LruCache<string, int>(2);
        cache.GetOrAdd("a", _ => 1);
        cache.GetOrAdd("b", _ => 2);
        cache.TryGet("a", out _); // a is now most recent

        cache.GetOrAdd("c", _ => 3);

        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Factory_runs_once_per_key()
    {
        var cache = new LruCache<string, int>(10);
        var calls = 0;

        cache.GetOrAdd("k", _ => ++calls);
        cache.GetOrAdd("k", _ => ++calls);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Uses_the_given_comparer()
    {
        var cache = new LruCache<string, int>(10, StringComparer.OrdinalIgnoreCase);
        cache.GetOrAdd("Key", _ => 1);

        Assert.Equal(1, cache.GetOrAdd("KEY", _ => 2));
    }

    [Fact]
    public void Clear_empties_the_cache()
    {
        var cache = new LruCache<int, int>(10);
        cache.GetOrAdd(1, k => k);

        cache.Clear();

        Assert.Equal(0, cache.Count);
    }
}
