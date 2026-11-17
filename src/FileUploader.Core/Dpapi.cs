using System.Runtime.InteropServices;
using System.Text;

namespace FileUploader.Core;

/// <summary>
/// 通过 Windows DPAPI（当前用户作用域）加解密字符串，避免明文保存密钥。
/// </summary>
internal static class Dpapi
{
    private const int CryptProtectUiForbidden = 0x1;

    private const string Entropy = "FileUploader.OSS.Settings.v1";

    /// <summary>最近一次 DPAPI 调用失败时的 Win32 错误码（诊断用）。</summary>
    internal static int LastError { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>加密为 Base64；失败时返回 null。</summary>
    public static string? Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return string.Empty;
        }

        var input = ToBlob(Encoding.UTF8.GetBytes(plainText));
        var entropy = ToBlob(Encoding.UTF8.GetBytes(Entropy));
        try
        {
            if (!CryptProtectData(ref input, "FileUploader secret", ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
            {
                LastError = Marshal.GetLastWin32Error();
                return null;
            }

            try
            {
                var result = new byte[output.CbData];
                Marshal.Copy(output.PbData, result, 0, output.CbData);
                return Convert.ToBase64String(result);
            }
            finally
            {
                FreeBlob(ref output);
            }
        }
        finally
        {
            FreeBlob(ref input);
            FreeBlob(ref entropy);
        }
    }

    /// <summary>解密 Base64；失败时返回 null。</summary>
    public static string? Unprotect(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return string.Empty;
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }

        var input = ToBlob(data);
        var entropy = ToBlob(Encoding.UTF8.GetBytes(Entropy));
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
            {
                LastError = Marshal.GetLastWin32Error();
                return null;
            }

            try
            {
                var result = new byte[output.CbData];
                Marshal.Copy(output.PbData, result, 0, output.CbData);
                return Encoding.UTF8.GetString(result);
            }
            finally
            {
                FreeBlob(ref output);
            }
        }
        finally
        {
            FreeBlob(ref input);
            FreeBlob(ref entropy);
        }
    }

    private static DataBlob ToBlob(byte[] data)
    {
        var blob = new DataBlob { CbData = data.Length, PbData = Marshal.AllocHGlobal(Math.Max(data.Length, 1)) };
        if (data.Length > 0)
        {
            Marshal.Copy(data, 0, blob.PbData, data.Length);
        }

        return blob;
    }

    private static void FreeBlob(ref DataBlob blob)
    {
        if (blob.PbData != IntPtr.Zero)
        {
            LocalFree(blob.PbData);
            blob.PbData = IntPtr.Zero;
        }
    }
}
