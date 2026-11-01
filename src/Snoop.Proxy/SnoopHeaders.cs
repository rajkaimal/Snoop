namespace Snoop.Proxy;

/// <summary>Header names Snoop itself uses. They are removed before a request is forwarded or recorded.</summary>
public static class SnoopHeaders
{
    /// <summary>Marks a request sent from Snoop's Composer; the value lets the Composer find its session.</summary>
    public const string ComposerId = "X-Snoop-Composer";
}
