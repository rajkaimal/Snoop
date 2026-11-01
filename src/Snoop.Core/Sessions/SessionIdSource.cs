namespace Snoop.Core.Sessions;

/// <summary>
/// Hands out session numbers. One source is shared by every proxy run of a capture and by
/// snapshot imports, so numbers never repeat while sessions are kept across restarts.
/// Thread-safe.
/// </summary>
public sealed class SessionIdSource
{
    private long _last;

    /// <summary>The next unused number (1, 2, 3, ...).</summary>
    public long Next() => Interlocked.Increment(ref _last);
}
