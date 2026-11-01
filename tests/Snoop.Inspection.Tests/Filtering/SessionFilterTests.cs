using Snoop.Core.Sessions;
using Snoop.Inspection.Filtering;
using static Snoop.Inspection.Tests.TestSessions;

namespace Snoop.Inspection.Tests.Filtering;

public class SessionFilterTests
{
    [Fact]
    public void Empty_filter_matches_everything()
    {
        Assert.True(SessionFilter.None.IsEmpty);
        Assert.True(SessionFilter.None.Matches(Create()));
        Assert.True(SessionFilter.None.Matches(Create(kind: SessionKind.Connect, method: "CONNECT")));
    }

    [Fact]
    public void Search_needs_every_word_to_match_somewhere()
    {
        var session = Create(url: "https://api.github.com/repos", process: "code", responseHeaders: Headers(("Content-Type", "application/json")));

        Assert.True(new SessionFilter { SearchText = "github" }.Matches(session));
        Assert.True(new SessionFilter { SearchText = "GITHUB json" }.Matches(session));
        Assert.True(new SessionFilter { SearchText = "code 200" }.Matches(session));
        Assert.False(new SessionFilter { SearchText = "github xml" }.Matches(session));
    }

    [Fact]
    public void Hide_tunnels()
    {
        var filter = new SessionFilter { HideTunnels = true };

        Assert.False(filter.Matches(Create(kind: SessionKind.Connect, method: "CONNECT")));
        Assert.True(filter.Matches(Create()));
    }

    [Fact]
    public void Only_errors_keeps_failures_and_4xx_5xx()
    {
        var filter = new SessionFilter { OnlyErrors = true };
        var failed = Create(status: null);
        failed.Fail("refused");

        Assert.True(filter.Matches(failed));
        Assert.True(filter.Matches(Create(status: 404)));
        Assert.True(filter.Matches(Create(status: 503)));
        Assert.False(filter.Matches(Create(status: 200)));
        Assert.False(filter.Matches(Create(status: 302)));
    }

    [Fact]
    public void Hide_static_drops_images_fonts_and_css()
    {
        var filter = new SessionFilter { HideStatic = true };

        Assert.False(filter.Matches(Create(responseHeaders: Headers(("Content-Type", "image/png")))));
        Assert.False(filter.Matches(Create(responseHeaders: Headers(("Content-Type", "font/woff2")))));
        Assert.False(filter.Matches(Create(responseHeaders: Headers(("Content-Type", "text/css")))));
        Assert.True(filter.Matches(Create(responseHeaders: Headers(("Content-Type", "application/json")))));
    }

    [Fact]
    public void Process_filter_matches_part_of_the_name()
    {
        var filter = new SessionFilter { ProcessName = "chr" };

        Assert.True(filter.Matches(Create(process: "chrome")));
        Assert.False(filter.Matches(Create(process: "msedge")));
        Assert.False(filter.Matches(Create(process: null)));
    }
}

public class SessionFilterScopeAndBodyTests
{
    [Fact]
    public void Host_filter_matches_the_exact_host()
    {
        var api = Create(url: "https://api.example.com/x");
        var sub = Create(url: "https://cdn.api.example.com/x");
        var filter = new SessionFilter { Host = "API.example.com" };

        Assert.False(filter.IsEmpty);
        Assert.True(filter.Matches(api));
        Assert.False(filter.Matches(sub));
    }

    [Fact]
    public void Body_search_only_when_asked()
    {
        var session = Create(responseHeaders: Headers(("Content-Type", "application/json")), responseBody: Utf8("{\"orderId\":\"A-7781\"}"));

        Assert.False(new SessionFilter { SearchText = "a-7781" }.Matches(session));
        Assert.True(new SessionFilter { SearchText = "a-7781", SearchBodies = true }.Matches(session));
        Assert.True(new SessionFilter { SearchText = "a-7781 200", SearchBodies = true }.Matches(session));
        Assert.False(new SessionFilter { SearchText = "nothere", SearchBodies = true }.Matches(session));
    }

    [Fact]
    public void Body_search_reads_request_bodies_and_decodes_gzip()
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest))
        {
            gzip.Write(Utf8("hidden needle here"));
        }

        var compressed = Create(responseHeaders: Headers(("Content-Encoding", "gzip")), responseBody: output.ToArray());
        var posted = Create(method: "POST", requestBody: Utf8("q=haystack"));

        Assert.True(new SessionFilter { SearchText = "needle", SearchBodies = true }.Matches(compressed));
        Assert.True(new SessionFilter { SearchText = "haystack", SearchBodies = true }.Matches(posted));
    }

    [Fact]
    public void Binary_bodies_are_not_searched()
    {
        var image = Create(responseHeaders: Headers(("Content-Type", "application/octet-stream")), responseBody: [0x89, 0x50, 0x4E, 0x47, 0, (byte)'P', (byte)'N', (byte)'G']);
        Assert.False(new SessionFilter { SearchText = "PNG", SearchBodies = true }.Matches(image));
    }

    [Fact]
    public void Search_words_split_on_spaces()
    {
        Assert.Equal(["a", "b"], SessionFilter.SearchWords("  a   b "));
        Assert.Empty(SessionFilter.SearchWords(null));
    }
}
