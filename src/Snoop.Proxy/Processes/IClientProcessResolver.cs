using System.Net;
using Snoop.Core.Sessions;

namespace Snoop.Proxy.Processes;

/// <summary>Finds which local process opened a client connection.</summary>
public interface IClientProcessResolver
{
    /// <summary>
    /// Returns the process that owns the TCP connection from <paramref name="client"/>
    /// to <paramref name="proxy"/>, or null if it cannot be found (e.g. a remote device).
    /// </summary>
    ClientProcessInfo? Resolve(IPEndPoint client, IPEndPoint proxy);
}

/// <summary>Never resolves anything. Used when process lookup is unavailable.</summary>
public sealed class NullClientProcessResolver : IClientProcessResolver
{
    public static NullClientProcessResolver Instance { get; } = new();

    public ClientProcessInfo? Resolve(IPEndPoint client, IPEndPoint proxy) => null;
}
