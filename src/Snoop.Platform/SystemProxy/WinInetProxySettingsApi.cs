using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Snoop.Platform.Interop.NativeMethods;

namespace Snoop.Platform.SystemProxy;

/// <summary>
/// The current user's WinINET proxy settings (Settings &gt; Network &gt; Proxy). Browsers and
/// most applications follow these. Changes take effect immediately for running applications.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinInetProxySettingsApi : IProxySettingsApi
{
    private static readonly int OptionSize = Marshal.SizeOf<INTERNET_PER_CONN_OPTION>();
    private static readonly int ListSize = Marshal.SizeOf<INTERNET_PER_CONN_OPTION_LIST>();

    public ProxySettings Read()
    {
        // FLAGS_UI reflects what the Settings app shows (incl. auto-detect); fall back to FLAGS on old systems.
        return TryRead(INTERNET_PER_CONN_FLAGS_UI) ?? TryRead(INTERNET_PER_CONN_FLAGS)
            ?? throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the system proxy settings.");
    }

    public void Write(ProxySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var strings = new List<IntPtr>();
        var options = Marshal.AllocHGlobal(OptionSize * 4);
        try
        {
            WriteOption(options, 0, INTERNET_PER_CONN_FLAGS, new INTERNET_PER_CONN_OPTION_VALUE { dwValue = (int)settings.Flags });
            WriteOption(options, 1, INTERNET_PER_CONN_PROXY_SERVER, StringValue(settings.ProxyServer, strings));
            WriteOption(options, 2, INTERNET_PER_CONN_PROXY_BYPASS, StringValue(settings.BypassList, strings));
            WriteOption(options, 3, INTERNET_PER_CONN_AUTOCONFIG_URL, StringValue(settings.AutoConfigUrl, strings));

            var list = new INTERNET_PER_CONN_OPTION_LIST
            {
                dwSize = ListSize,
                pszConnection = IntPtr.Zero, // The default (LAN) connection, used for all connections on modern Windows.
                dwOptionCount = 4,
                pOptions = options,
            };

            if (!InternetSetOption(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, ref list, ListSize))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not change the system proxy settings.");
            }

            // Tell running applications to reload.
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
        finally
        {
            foreach (var pointer in strings)
            {
                Marshal.FreeHGlobal(pointer);
            }

            Marshal.FreeHGlobal(options);
        }
    }

    private static ProxySettings? TryRead(int flagsOption)
    {
        var options = Marshal.AllocHGlobal(OptionSize * 4);
        try
        {
            WriteOption(options, 0, flagsOption, default);
            WriteOption(options, 1, INTERNET_PER_CONN_PROXY_SERVER, default);
            WriteOption(options, 2, INTERNET_PER_CONN_PROXY_BYPASS, default);
            WriteOption(options, 3, INTERNET_PER_CONN_AUTOCONFIG_URL, default);

            var list = new INTERNET_PER_CONN_OPTION_LIST
            {
                dwSize = ListSize,
                pszConnection = IntPtr.Zero,
                dwOptionCount = 4,
                pOptions = options,
            };

            var size = ListSize;
            if (!InternetQueryOption(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, ref list, ref size))
            {
                return null;
            }

            var flags = (ProxyFlags)ReadOption(options, 0).Value.dwValue;
            return new ProxySettings(
                flags,
                TakeString(ReadOption(options, 1)),
                TakeString(ReadOption(options, 2)),
                TakeString(ReadOption(options, 3)));
        }
        finally
        {
            Marshal.FreeHGlobal(options);
        }
    }

    private static void WriteOption(IntPtr options, int index, int option, INTERNET_PER_CONN_OPTION_VALUE value) =>
        Marshal.StructureToPtr(new INTERNET_PER_CONN_OPTION { dwOption = option, Value = value }, options + (index * OptionSize), false);

    private static INTERNET_PER_CONN_OPTION ReadOption(IntPtr options, int index) =>
        Marshal.PtrToStructure<INTERNET_PER_CONN_OPTION>(options + (index * OptionSize));

    private static INTERNET_PER_CONN_OPTION_VALUE StringValue(string? value, List<IntPtr> allocations)
    {
        if (string.IsNullOrEmpty(value))
        {
            return new INTERNET_PER_CONN_OPTION_VALUE { pszValue = IntPtr.Zero };
        }

        var pointer = Marshal.StringToHGlobalUni(value);
        allocations.Add(pointer);
        return new INTERNET_PER_CONN_OPTION_VALUE { pszValue = pointer };
    }

    /// <summary>Reads a string WinINET allocated for us, then frees it.</summary>
    private static string? TakeString(INTERNET_PER_CONN_OPTION option)
    {
        var pointer = option.Value.pszValue;
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var text = Marshal.PtrToStringUni(pointer);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        finally
        {
            GlobalFree(pointer);
        }
    }
}
