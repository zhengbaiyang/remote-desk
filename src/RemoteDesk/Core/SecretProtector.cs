using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RemoteDesk;

internal enum SecretProtectionScope
{
    CurrentUser,
    LocalMachine,
}

internal static class SecretProtector
{
    private const string UserPrefix = "dpapi-u:";
    private const string MachinePrefix = "dpapi-m:";
    private const string PlainFallbackPrefix = "plain:";

    public static string ProtectCurrentUser(string plaintext) =>
        Protect(plaintext, SecretProtectionScope.CurrentUser);

    public static string ProtectLocalMachine(string plaintext) =>
        Protect(plaintext, SecretProtectionScope.LocalMachine);

    public static string Protect(string plaintext, SecretProtectionScope scope)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (plaintext.Length == 0)
            return "";

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        if (!OperatingSystem.IsWindows())
            return PlainFallbackPrefix + Convert.ToBase64String(bytes);

        var flags = CryptProtectUiForbidden | (scope == SecretProtectionScope.LocalMachine ? CryptProtectLocalMachine : 0);
        var prefix = scope == SecretProtectionScope.LocalMachine ? MachinePrefix : UserPrefix;
        var protectedBytes = ProtectBytesWindows(bytes, flags);
        return prefix + Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
            return "";

        if (protectedValue.StartsWith(PlainFallbackPrefix, StringComparison.Ordinal))
        {
            var raw = Convert.FromBase64String(protectedValue[PlainFallbackPrefix.Length..]);
            return Encoding.UTF8.GetString(raw);
        }

        if (protectedValue.StartsWith(UserPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("无法在非 Windows 平台解密 DPAPI 凭据。");
            var cipher = Convert.FromBase64String(protectedValue[UserPrefix.Length..]);
            return Encoding.UTF8.GetString(UnprotectBytesWindows(cipher));
        }

        if (protectedValue.StartsWith(MachinePrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("无法在非 Windows 平台解密 DPAPI 凭据。");
            var cipher = Convert.FromBase64String(protectedValue[MachinePrefix.Length..]);
            return Encoding.UTF8.GetString(UnprotectBytesWindows(cipher));
        }

        // Legacy plain value compatibility if unprefixed.
        return protectedValue;
    }

    public static bool TryUnprotect(string? protectedValue, out string plaintext)
    {
        plaintext = "";
        if (string.IsNullOrEmpty(protectedValue))
            return false;

        try
        {
            plaintext = Unprotect(protectedValue);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or Win32Exception or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static byte[] ProtectBytesWindows(byte[] plainBytes, uint flags)
    {
        var inputBlob = default(DataBlob);
        var outputBlob = default(DataBlob);
        var inputHandle = GCHandle.Alloc(plainBytes, GCHandleType.Pinned);
        try
        {
            inputBlob.Size = plainBytes.Length;
            inputBlob.Data = inputHandle.AddrOfPinnedObject();

            if (!CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, flags, out outputBlob))
                throw new CryptographicException(Marshal.GetLastWin32Error());

            var result = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, result, 0, outputBlob.Size);
            return result;
        }
        finally
        {
            if (inputHandle.IsAllocated)
                inputHandle.Free();
            if (outputBlob.Data != IntPtr.Zero)
                LocalFree(outputBlob.Data);
        }
    }

    private static byte[] UnprotectBytesWindows(byte[] cipherBytes)
    {
        var inputBlob = default(DataBlob);
        var outputBlob = default(DataBlob);
        var inputHandle = GCHandle.Alloc(cipherBytes, GCHandleType.Pinned);
        try
        {
            inputBlob.Size = cipherBytes.Length;
            inputBlob.Data = inputHandle.AddrOfPinnedObject();

            if (!CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob))
                throw new CryptographicException(Marshal.GetLastWin32Error());

            var result = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, result, 0, outputBlob.Size);
            return result;
        }
        finally
        {
            if (inputHandle.IsAllocated)
                inputHandle.Free();
            if (outputBlob.Data != IntPtr.Zero)
                LocalFree(outputBlob.Data);
        }
    }

    private const uint CryptProtectUiForbidden = 0x1;
    private const uint CryptProtectLocalMachine = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? dataDescr,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr dataDescr,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);
}
