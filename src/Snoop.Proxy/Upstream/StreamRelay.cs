namespace Snoop.Proxy.Upstream;

/// <summary>Byte counts from a finished relay.</summary>
public readonly record struct RelayResult(long ClientToServer, long ServerToClient);

/// <summary>Copies bytes both ways between two streams until either side closes.</summary>
public static class StreamRelay
{
    /// <param name="client">The app side.</param>
    /// <param name="server">The server side.</param>
    /// <param name="cancellationToken">Stops the relay.</param>
    /// <param name="watchClient">Sees every block sent by the app, after it was forwarded. Must not throw.</param>
    /// <param name="watchServer">Sees every block sent by the server, after it was forwarded. Must not throw.</param>
    public static async Task<RelayResult> RunAsync(
        Stream client,
        Stream server,
        CancellationToken cancellationToken,
        Action<ReadOnlyMemory<byte>>? watchClient = null,
        Action<ReadOnlyMemory<byte>>? watchServer = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(server);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var up = PumpAsync(client, server, watchClient, stop.Token);
        var down = PumpAsync(server, client, watchServer, stop.Token);

        // When one direction ends, stop the other: a half-closed tunnel is treated as closed.
        await Task.WhenAny(up, down).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        var sent = await up.ConfigureAwait(false);
        var received = await down.ConfigureAwait(false);
        return new RelayResult(sent, received);
    }

    private static async Task<long> PumpAsync(Stream from, Stream to, Action<ReadOnlyMemory<byte>>? watch, CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        long total = 0;
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await to.FlushAsync(cancellationToken).ConfigureAwait(false);
                total += read;
                watch?.Invoke(buffer.AsMemory(0, read));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Either side went away; the byte count so far is still accurate.
        }

        return total;
    }
}
