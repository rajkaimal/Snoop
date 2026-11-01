using System.Collections;
using System.Text;

namespace Snoop.Core.Http;

/// <summary>
/// An ordered list of header fields. Keeps duplicates and original order and casing,
/// so a captured message can be shown exactly as it was sent. Lookups ignore case.
/// </summary>
public sealed class HttpHeaderList : IReadOnlyList<HttpHeader>
{
    private readonly List<HttpHeader> _headers;

    public HttpHeaderList() => _headers = [];

    public HttpHeaderList(IEnumerable<HttpHeader> headers) => _headers = [.. headers];

    public int Count => _headers.Count;

    public HttpHeader this[int index] => _headers[index];

    public void Add(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        _headers.Add(new HttpHeader(name, value));
    }

    public void Add(HttpHeader header) => Add(header.Name, header.Value);

    /// <summary>Returns true when at least one field with this name exists.</summary>
    public bool Contains(string name) => _headers.Exists(h => Matches(h, name));

    /// <summary>Returns the first value for <paramref name="name"/>, or null.</summary>
    public string? Get(string name)
    {
        foreach (var header in _headers)
        {
            if (Matches(header, name))
            {
                return header.Value;
            }
        }

        return null;
    }

    /// <summary>Returns every value for <paramref name="name"/>, in order.</summary>
    public IReadOnlyList<string> GetAll(string name) =>
        _headers.Where(h => Matches(h, name)).Select(h => h.Value).ToList();

    /// <summary>
    /// Returns the comma-separated list items of every field with this name,
    /// trimmed and with empty items removed (e.g. <c>Connection: keep-alive, Upgrade</c>).
    /// </summary>
    public IReadOnlyList<string> GetListItems(string name)
    {
        var items = new List<string>();
        foreach (var value in GetAll(name))
        {
            foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                items.Add(part);
            }
        }

        return items;
    }

    /// <summary>True when a comma-separated header contains <paramref name="token"/> (case-insensitive).</summary>
    public bool HasToken(string name, string token) =>
        GetListItems(name).Any(item => string.Equals(item, token, StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes every field with this name. Returns how many were removed.</summary>
    public int Remove(string name) => _headers.RemoveAll(h => Matches(h, name));

    /// <summary>Replaces all fields with this name by a single field.</summary>
    public void Set(string name, string value)
    {
        var index = _headers.FindIndex(h => Matches(h, name));
        Remove(name);
        var header = new HttpHeader(name, value);
        if (index < 0 || index > _headers.Count)
        {
            _headers.Add(header);
        }
        else
        {
            _headers.Insert(index, header);
        }
    }

    public HttpHeaderList Clone() => new(_headers);

    /// <summary>Header block as text: one <c>Name: value</c> line per field, CRLF separated.</summary>
    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var header in _headers)
        {
            builder.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        }

        return builder.ToString();
    }

    public IEnumerator<HttpHeader> GetEnumerator() => _headers.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static bool Matches(HttpHeader header, string name) =>
        string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase);
}
