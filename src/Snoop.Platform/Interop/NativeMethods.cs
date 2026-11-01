using System.Runtime.InteropServices;

namespace Snoop.Platform.Interop;

/// <summary>Win32 declarations. Only call these on Windows.</summary>
internal static partial class NativeMethods
{
    // ------------------------------------------------------------ WinINET (system proxy)

    public const int INTERNET_OPTION_REFRESH = 37;
    public const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    public const int INTERNET_OPTION_PER_CONNECTION_OPTION = 75;

    public const int INTERNET_PER_CONN_FLAGS = 1;
    public const int INTERNET_PER_CONN_PROXY_SERVER = 2;
    public const int INTERNET_PER_CONN_PROXY_BYPASS = 3;
    public const int INTERNET_PER_CONN_AUTOCONFIG_URL = 4;
    public const int INTERNET_PER_CONN_FLAGS_UI = 10;

    [StructLayout(LayoutKind.Sequential)]
    public struct INTERNET_PER_CONN_OPTION_LIST
    {
        public int dwSize;
        public IntPtr pszConnection;
        public int dwOptionCount;
        public int dwOptionError;
        public IntPtr pOptions;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INTERNET_PER_CONN_OPTION
    {
        public int dwOption;
        public INTERNET_PER_CONN_OPTION_VALUE Value;
    }

    /// <summary>The native union { DWORD; LPWSTR; FILETIME }. FILETIME is two DWORDs, so it is declared as such to keep 4-byte alignment on x86.</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct INTERNET_PER_CONN_OPTION_VALUE
    {
        [FieldOffset(0)]
        public int dwValue;

        [FieldOffset(0)]
        public IntPtr pszValue;

        [FieldOffset(0)]
        public int ftLowDateTime;

        [FieldOffset(4)]
        public int ftHighDateTime;
    }

    [LibraryImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InternetQueryOption(IntPtr hInternet, int dwOption, ref INTERNET_PER_CONN_OPTION_LIST lpBuffer, ref int lpdwBufferLength);

    [LibraryImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InternetSetOption(IntPtr hInternet, int dwOption, ref INTERNET_PER_CONN_OPTION_LIST lpBuffer, int dwBufferLength);

    [LibraryImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalFree(IntPtr hMem);

    // ------------------------------------------------------------ DPAPI

    public const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        IntPtr szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr LocalFree(IntPtr hMem);

    // ------------------------------------------------------------ IP Helper (TCP owner lookup)

    public const int AF_INET = 2;
    public const int AF_INET6 = 23;
    public const int TCP_TABLE_OWNER_PID_ALL = 5;
    public const int NO_ERROR = 0;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    public static partial int GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        int tableClass,
        int reserved);
}
