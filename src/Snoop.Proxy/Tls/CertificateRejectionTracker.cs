using System.Collections.Concurrent;

namespace Snoop.Proxy.Tls;

/// <summary>
/// Remembers hosts whose clients keep rejecting Snoop's certificate (apps that pin
/// certificates or ship their own trust store, like the Claude and Google Drive desktop apps).
/// After <c>threshold</c> rejections in a row a host is skipped: its tunnels are relayed
/// without decryption for the rest of the run, so the app recovers by itself.
/// A successfully decrypted request resets the count.
/// </summary>
public sealed class CertificateRejectionTracker(int threshold)
{
    private readonly ConcurrentDictionary<string, int> _rejections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _skipped = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rejections in a row before a host is skipped. 0 turns automatic skipping off.</summary>
    public int Threshold { get; } = threshold >= 0 ? threshold : throw new ArgumentOutOfRangeException(nameof(threshold));

    /// <summary>Hosts skipped so far, with when they were skipped.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> SkippedHosts => _skipped;

    public bool IsSkipped(string host) => _skipped.ContainsKey(Normalize(host));

    /// <summary>Records a rejection. Returns true when this rejection made the host skipped.</summary>
    public bool RecordRejection(string host)
    {
        if (Threshold == 0)
        {
            return false;
        }

        var key = Normalize(host);
        var count = _rejections.AddOrUpdate(key, 1, static (_, previous) => previous + 1);
        return count >= Threshold && _skipped.TryAdd(key, DateTimeOffset.UtcNow);
    }

    /// <summary>Skips a host right away (e.g. the user chose "don't decrypt this host"). Returns false if already skipped.</summary>
    public bool Skip(string host) => _skipped.TryAdd(Normalize(host), DateTimeOffset.UtcNow);

    /// <summary>A request got through the decrypted tunnel: the client trusts us for this host.</summary>
    public void RecordSuccess(string host) => _rejections.TryRemove(Normalize(host), out _);

    private static string Normalize(string host) => CertificateAuthority.NormalizeHost(host);
}
