using System.Text;
using Snoop.Core.Http;
using Snoop.Core.Sessions;
using Snoop.Inspection.Export;

namespace Snoop.Inspection.Tests.Export;

public sealed class CurlImporterTests
{
    private static CurlRequest Parse(string command)
    {
        Assert.True(CurlImporter.TryParse(command, out var request, out var error), error);
        return request!;
    }

    [Fact]
    public void Chrome_bash_copy()
    {
        var request = Parse("""
            curl 'https://api.example.com/v1/items?page=2' \
              -H 'accept: application/json' \
              -H 'authorization: Bearer abc.def' \
              -H $'x-note: it\'s here' \
              --data-raw '{"name":"Ann \"A\""}' \
              --compressed
            """);

        Assert.Equal("POST", request.Method);
        Assert.Equal("https://api.example.com/v1/items?page=2", request.Url);
        Assert.Equal("application/json", request.Headers.Get("accept"));
        Assert.Equal("Bearer abc.def", request.Headers.Get("authorization"));
        Assert.Equal("it's here", request.Headers.Get("x-note"));
        Assert.Equal("{\"name\":\"Ann \\\"A\\\"\"}", request.Body);
        Assert.Equal("application/x-www-form-urlencoded", request.Headers.Get("Content-Type"));
        Assert.Empty(request.Warnings);
    }

    [Fact]
    public void Chrome_cmd_copy()
    {
        var request = Parse("curl ^\"https://example.com/search?q=a^%^20b^\" ^\r\n  -H ^\"accept: */*^\" ^\r\n  --data-raw ^\"^{^\\^\"k^\\^\":1^}^\"");

        Assert.Equal("https://example.com/search?q=a%20b", request.Url);
        Assert.Equal("*/*", request.Headers.Get("accept"));
        Assert.Equal("{\"k\":1}", request.Body);
    }

    [Fact]
    public void Short_options_values_and_groups()
    {
        var request = Parse("curl -sSL -XPUT -H\"X-A: 1\" -d a=1 -d b=2 -u ann:secret -A agent/1 -e https://ref/ -b 'c=1' http://x.test/p");

        Assert.Equal("PUT", request.Method);
        Assert.Equal("http://x.test/p", request.Url);
        Assert.Equal("1", request.Headers.Get("X-A"));
        Assert.Equal("a=1&b=2", request.Body);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ann:secret")), request.Headers.Get("Authorization"));
        Assert.Equal("agent/1", request.Headers.Get("User-Agent"));
        Assert.Equal("https://ref/", request.Headers.Get("Referer"));
        Assert.Equal("c=1", request.Headers.Get("Cookie"));
    }

    [Fact]
    public void Get_with_data_goes_in_the_query()
    {
        var request = Parse("curl -G --data-urlencode 'q=a b&c' -d n=1 'https://x.test/s?x=0'");
        Assert.Equal("GET", request.Method);
        Assert.Equal("https://x.test/s?x=0&q=a%20b%26c&n=1", request.Url);
        Assert.Null(request.Body);
    }

    [Fact]
    public void Json_option_sets_the_content_type()
    {
        var request = Parse("curl --json '{\"a\":1}' https://x.test/");
        Assert.Equal("POST", request.Method);
        Assert.Equal("application/json", request.Headers.Get("Content-Type"));
        Assert.Equal("application/json", request.Headers.Get("Accept"));
    }

    [Fact]
    public void Head_and_explicit_method()
    {
        Assert.Equal("HEAD", Parse("curl -I https://x.test/").Method);
        Assert.Equal("DELETE", Parse("curl --request delete https://x.test/").Method);
        Assert.Equal("GET", Parse("curl https://x.test/").Method);
    }

    [Fact]
    public void Unsupported_options_become_warnings()
    {
        var request = Parse("curl -F file=@a.png -d @body.json --weird https://x.test/ -o out.txt");
        Assert.Equal(3, request.Warnings.Count);
        Assert.Contains(request.Warnings, w => w.Contains("Multipart", StringComparison.Ordinal));
        Assert.Contains(request.Warnings, w => w.Contains("body.json", StringComparison.Ordinal));
        Assert.Contains(request.Warnings, w => w.Contains("--weird", StringComparison.Ordinal));
        Assert.Null(request.Body);
    }

    [Theory]
    [InlineData("wget https://x", "not a cURL command")]
    [InlineData("curl -H 'a: b'", "no URL")]
    [InlineData("curl 'https://x", "not closed")]
    [InlineData("curl https://x -H", "needs a value")]
    public void Explains_problems(string command, string expected)
    {
        Assert.False(CurlImporter.TryParse(command, out _, out var error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Round_trips_the_exporter()
    {
        var headers = new HttpHeaderList();
        headers.Add("Host", "api.example.com");
        headers.Add("Content-Type", "application/json");
        headers.Add("X-Quote", "it's");
        var info = new RequestInfo("PATCH", new Uri("https://api.example.com/a?b=1"), "HTTP/1.1", headers)
        {
            Body = Snoop.Core.Bodies.CapturedBody.FromBytes(Encoding.UTF8.GetBytes("{\"x\":\"y'z\"}")),
        };

        var request = Parse(CommandExporter.ToCurl(info));

        Assert.Equal("PATCH", request.Method);
        Assert.Equal("https://api.example.com/a?b=1", request.Url);
        Assert.Equal("it's", request.Headers.Get("X-Quote"));
        Assert.Equal("{\"x\":\"y'z\"}", request.Body);
    }
}
