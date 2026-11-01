using Snoop.Core.Http;

namespace Snoop.Core.Tests.Http;

public class HttpHeaderListTests
{
    [Fact]
    public void Keeps_order_duplicates_and_original_casing()
    {
        var headers = new HttpHeaderList();
        headers.Add("Set-Cookie", "a=1");
        headers.Add("content-type", "text/plain");
        headers.Add("Set-Cookie", "b=2");

        Assert.Equal(3, headers.Count);
        Assert.Equal("Set-Cookie", headers[0].Name);
        Assert.Equal("content-type", headers[1].Name);
        Assert.Equal(new[] { "a=1", "b=2" }, headers.GetAll("set-cookie"));
    }

    [Fact]
    public void Get_ignores_case_and_returns_first_value()
    {
        var headers = new HttpHeaderList([new("Accept", "a"), new("ACCEPT", "b")]);

        Assert.Equal("a", headers.Get("accept"));
        Assert.Null(headers.Get("Missing"));
        Assert.True(headers.Contains("aCcEpT"));
    }

    [Fact]
    public void GetListItems_splits_commas_across_fields()
    {
        var headers = new HttpHeaderList([new("Connection", "keep-alive, Upgrade"), new("Connection", " x-custom ,")]);

        Assert.Equal(new[] { "keep-alive", "Upgrade", "x-custom" }, headers.GetListItems("connection"));
        Assert.True(headers.HasToken("Connection", "upgrade"));
        Assert.False(headers.HasToken("Connection", "close"));
    }

    [Fact]
    public void Remove_deletes_every_field_with_the_name()
    {
        var headers = new HttpHeaderList([new("A", "1"), new("B", "2"), new("a", "3")]);

        Assert.Equal(2, headers.Remove("A"));
        Assert.Single(headers);
        Assert.Equal("B", headers[0].Name);
    }

    [Fact]
    public void Set_replaces_in_place_of_the_first_occurrence()
    {
        var headers = new HttpHeaderList([new("A", "1"), new("Host", "old"), new("B", "2"), new("host", "dup")]);

        headers.Set("Host", "new");

        Assert.Equal(new[] { "A", "Host", "B" }, headers.Select(h => h.Name));
        Assert.Equal("new", headers.Get("host"));
    }

    [Fact]
    public void Set_appends_when_missing()
    {
        var headers = new HttpHeaderList([new("A", "1")]);

        headers.Set("B", "2");

        Assert.Equal("B", headers[1].Name);
    }

    [Fact]
    public void ToString_renders_wire_format()
    {
        var headers = new HttpHeaderList([new("A", "1"), new("B", "two")]);

        Assert.Equal("A: 1\r\nB: two\r\n", headers.ToString());
    }

    [Fact]
    public void Clone_is_independent()
    {
        var original = new HttpHeaderList([new("A", "1")]);
        var copy = original.Clone();

        copy.Add("B", "2");

        Assert.Single(original);
        Assert.Equal(2, copy.Count);
    }

    [Fact]
    public void Add_rejects_empty_name()
    {
        var headers = new HttpHeaderList();

        Assert.Throws<ArgumentException>(() => headers.Add(string.Empty, "x"));
    }
}
