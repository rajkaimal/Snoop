using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Snoop.Proxy.Tls;
using static Snoop.Platform.Interop.NativeMethods;

namespace Snoop.Platform.Security;

/// <summary>
/// Encrypts secrets with Windows DPAPI for the current user: only this Windows account on
/// this machine can decrypt them. Protects the root certificate's private key at rest.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] data) => Transform(data, protect: true);

    public byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        ArgumentNullException.ThrowIfNull(data);
        var input = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        var output = default(DATA_BLOB);
        try
        {
            Marshal.Copy(data, 0, input.pbData, data.Length);
            var ok = protect
                ? CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new System.Security.Cryptography.CryptographicException(
                    $"DPAPI {(protect ? "encryption" : "decryption")} failed.",
                    new Win32Exception(error));
            }

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            // Clear the plaintext copy before releasing it.
            unsafe
            {
                new Span<byte>((void*)input.pbData, data.Length).Clear();
            }

            Marshal.FreeHGlobal(input.pbData);
            if (output.pbData != IntPtr.Zero)
            {
                LocalFree(output.pbData);
            }
        }
    }
}
