using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Snoop.Core.Sessions;
using Snoop.Proxy.Processes;
using static Snoop.Platform.Interop.NativeMethods;

namespace Snoop.Platform.Processes;

/// <summary>
/// Finds the process behind a local client connection using the Windows TCP table
/// (<c>GetExtendedTcpTable</c>). Remote clients resolve to null.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessResolver : IClientProcessResolver
{
    private const int Ipv4RowSize = 24;
    private const int Ipv6RowSize = 56;
    private static readonly TimeSpan NameCacheLifetime = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<int, (string Name, long Expires)> _names = new();

    public ClientProcessInfo? Resolve(IPEndPoint client, IPEndPoint proxy)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(proxy);
        var clientAddress = Normalize(client.Address);
        if (!IPAddress.IsLoopback(clientAddress) && !IsLocalAddress(clientAddress))
        {
            return null;
        }

        var pid = clientAddress.AddressFamily == AddressFamily.InterNetworkV6
            ? FindOwnerIpv6(clientAddress, client.Port, proxy.Port)
            : FindOwnerIpv4(clientAddress, client.Port, proxy.Port);

        return pid is null or 0 ? null : new ClientProcessInfo(pid.Value, GetProcessName(pid.Value));
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool IsLocalAddress(IPAddress address)
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName()).Any(a => Normalize(a).Equals(address));
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int? FindOwnerIpv4(IPAddress address, int localPort, int remotePort)
    {
        var table = ReadTable(AF_INET);
        if (table is null)
        {
            return null;
        }

        var addressBytes = address.GetAddressBytes();
        var count = BinaryPrimitives.ReadInt32LittleEndian(table);
        for (var i = 0; i < count; i++)
        {
            // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
            var row = table.AsSpan(4 + (i * Ipv4RowSize), Ipv4RowSize);
            if (row.Slice(4, 4).SequenceEqual(addressBytes)
                && ReadPort(row.Slice(8, 4)) == localPort
                && ReadPort(row.Slice(16, 4)) == remotePort)
            {
                return BinaryPrimitives.ReadInt32LittleEndian(row.Slice(20, 4));
            }
        }

        return null;
    }

    private static int? FindOwnerIpv6(IPAddress address, int localPort, int remotePort)
    {
        var table = ReadTable(AF_INET6);
        if (table is null)
        {
            return null;
        }

        var addressBytes = address.GetAddressBytes();
        var count = BinaryPrimitives.ReadInt32LittleEndian(table);
        for (var i = 0; i < count; i++)
        {
            // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
            var row = table.AsSpan(4 + (i * Ipv6RowSize), Ipv6RowSize);
            if (row[..16].SequenceEqual(addressBytes)
                && ReadPort(row.Slice(20, 4)) == localPort
                && ReadPort(row.Slice(44, 4)) == remotePort)
            {
                return BinaryPrimitives.ReadInt32LittleEndian(row.Slice(52, 4));
            }
        }

        return null;
    }

    /// <summary>Ports are stored in network byte order in the low 16 bits of a DWORD.</summary>
    private static int ReadPort(ReadOnlySpan<byte> field) => (field[0] << 8) | field[1];

    private static byte[]? ReadTable(int addressFamily)
    {
        var size = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = size == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, addressFamily, TCP_TABLE_OWNER_PID_ALL, 0);
                if (result == NO_ERROR && buffer != IntPtr.Zero)
                {
                    var copy = new byte[size];
                    Marshal.Copy(buffer, copy, 0, size);
                    return copy;
                }

                if (result != ERROR_INSUFFICIENT_BUFFER && !(result == NO_ERROR && buffer == IntPtr.Zero))
                {
                    return null;
                }

                size += 4096; // The table can grow between calls.
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        return null;
    }

    private string GetProcessName(int pid)
    {
        var now = Stopwatch.GetTimestamp();
        if (_names.TryGetValue(pid, out var cached) && cached.Expires > now)
        {
            return cached.Name;
        }

        string name;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch (ArgumentException)
        {
            name = "exited";
        }
        catch (InvalidOperationException)
        {
            name = "unknown";
        }

        _names[pid] = (name, now + (long)(NameCacheLifetime.TotalSeconds * Stopwatch.Frequency));
        return name;
    }
}
