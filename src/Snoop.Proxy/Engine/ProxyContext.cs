using System.Net;
using Snoop.Core.Diagnostics;
using Snoop.Core.Sessions;
using Snoop.Proxy.Interception;
using Snoop.Proxy.Processes;
using Snoop.Proxy.Tls;
using Snoop.Proxy.Upstream;
using Snoop.Proxy.Util;

namespace Snoop.Proxy.Engine;

/// <summary>Everything a client connection needs, shared by all connections of one proxy run.</summary>
internal sealed class ProxyContext(
    ProxyOptions options,
    ISessionSink sink,
    CertificateAuthority? certificateAuthority,
    IClientProcessResolver processResolver,
    ISnoopLogger logger,
    SessionIdSource sessionIds,
    ITrafficInterceptor? interceptor)
{
    public ProxyOptions Options { get; } = options;

    public CertificateAuthority? CertificateAuthority { get; } = certificateAuthority;

    public IClientProcessResolver ProcessResolver { get; } = processResolver;

    public ISnoopLogger Logger { get; } = logger;

    /// <summary>Rules and breakpoints, or null when nothing may change traffic.</summary>
    public ITrafficInterceptor? Interceptor { get; } = interceptor;

    public UpstreamHttpClient Upstream { get; } = new(options);

    public RawUpstreamConnector RawConnector { get; } = new(options);

    public HostMatcher DoNotDecrypt { get; } = new(
        options.UseBuiltInDoNotDecryptList
            ? [.. options.DoNotDecryptHosts, .. ProxyOptions.BuiltInDoNotDecryptHosts]
            : options.DoNotDecryptHosts);

    public CertificateRejectionTracker Rejections { get; } = new(options.AutoSkipAfterRejections);

    public bool CanDecrypt => Options.DecryptHttps && CertificateAuthority is not null;

    /// <summary>Whether tunnels to <paramref name="host"/> should be decrypted.</summary>
    public bool ShouldDecrypt(string host) => CanDecrypt && !DoNotDecrypt.IsMatch(host) && !Rejections.IsSkipped(host);

    /// <summary>Records that a client rejected our certificate for <paramref name="host"/>.</summary>
    public void ReportCertificateRejected(string host)
    {
        if (Rejections.RecordRejection(host))
        {
            Logger.Warn($"Stopped decrypting {host}: its app rejected the Snoop certificate {Rejections.Threshold} times in a row. "
                        + "Its traffic now passes through without decryption.");
        }
    }

    /// <summary>The endpoint the proxy is actually listening on (set once started).</summary>
    public IPEndPoint ListenEndpoint { get; set; } = new(IPAddress.Loopback, 0);

    public long NextSessionId() => sessionIds.Next();

    /// <summary>True when a request URL points back at the proxy itself.</summary>
    public bool IsSelf(string host, int port)
    {
        if (port != ListenEndpoint.Port)
        {
            return false;
        }

        var normalized = host.Trim('[', ']');
        if (string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(normalized, out var address))
        {
            return false;
        }

        return IPAddress.IsLoopback(address)
            || address.Equals(ListenEndpoint.Address)
            || (ListenEndpoint.Address.Equals(IPAddress.Any) && IsLocalInterfaceAddress(address));
    }

    public void Started(Session session) => Notify(session, static (s, x) => s.OnSessionStarted(x));

    public void Updated(Session session) => Notify(session, static (s, x) => s.OnSessionUpdated(x));

    public void Finished(Session session) => Notify(session, static (s, x) => s.OnSessionFinished(x));

    private void Notify(Session session, Action<ISessionSink, Session> call)
    {
        try
        {
            call(sink, session);
        }
#pragma warning disable CA1031 // A faulty listener must never break traffic.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Logger.Error($"Session sink failed for session #{session.Id}.", ex);
        }
    }

    private static bool IsLocalInterfaceAddress(IPAddress address)
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(a => a.Address.Equals(address));
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return false;
        }
    }
}
