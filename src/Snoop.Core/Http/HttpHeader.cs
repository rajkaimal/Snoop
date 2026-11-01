namespace Snoop.Core.Http;

/// <summary>A single HTTP header field exactly as it appeared on the wire.</summary>
public readonly record struct HttpHeader(string Name, string Value)
{
    public override string ToString() => $"{Name}: {Value}";
}
