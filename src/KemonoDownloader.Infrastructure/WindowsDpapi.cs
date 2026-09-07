using System.Runtime.InteropServices;
using System.Text;

namespace KemonoDownloader.Infrastructure;

internal static class WindowsDpapi
{
    private const int CryptProtectUiForbidden = 0x1;

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var input = Encoding.UTF8.GetBytes(value);
        var inputBlob = CreateBlob(input);
        try
        {
            if (!CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var outputBlob))
            {
                throw new InvalidOperationException($"DPAPI 加密失败，错误码 {Marshal.GetLastWin32Error()}。");
            }
            try
            {
                var output = new byte[outputBlob.Size];
                Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                return Convert.ToBase64String(output);
            }
            finally
            {
                LocalFree(outputBlob.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputBlob.Data);
        }
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        try
        {
            var input = Convert.FromBase64String(value);
            var inputBlob = CreateBlob(input);
            try
            {
                if (!CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var outputBlob))
                {
                    throw new InvalidOperationException($"DPAPI 解密失败，错误码 {Marshal.GetLastWin32Error()}。");
                }
                try
                {
                    var output = new byte[outputBlob.Size];
                    Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                    return Encoding.UTF8.GetString(output);
                }
                finally
                {
                    LocalFree(outputBlob.Data);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(inputBlob.Data);
            }
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = pointer };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
