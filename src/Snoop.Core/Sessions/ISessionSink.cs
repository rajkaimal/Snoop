namespace Snoop.Core.Sessions;

/// <summary>
/// Receives sessions from the proxy engine as they progress. Calls arrive on
/// connection threads, concurrently; implementations must be thread-safe and fast.
/// </summary>
public interface ISessionSink
{
    /// <summary>A new session has started (request head received or tunnel opened).</summary>
    void OnSessionStarted(Session session);

    /// <summary>A running session changed (response headers arrived, tunnel decrypted...).</summary>
    void OnSessionUpdated(Session session);

    /// <summary>The session has finished; it will not change again.</summary>
    void OnSessionFinished(Session session);
}
