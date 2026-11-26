using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MCSMServerManager;

/// <summary>
/// Windows DPAPI（CryptProtectData / CryptUnprotectData）的最小封装，用于把 API Key
/// 以「当前 Windows 用户才能解密」的形式落盘，而不是明文写进 config.json。
///
/// 这里直接 P/Invoke 而不用 System.Security.Cryptography.ProtectedData，是为了不给
/// 自包含单文件发布引入额外的 NuGet 依赖（审计 B-03 / I-03 都关注供应链面）。
///
/// 任何一步失败都返回 null，由调用方决定降级策略；本类自身不抛异常。
/// </summary>
internal static class Dpapi
{
    /// <summary>CRYPTPROTECT_UI_FORBIDDEN：绝不允许弹出 UI 询问用户。</summary>
    private const int CryptprotectUiForbidden = 0x1;

    private const string Description = "MCSMServerManager API Key";

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);

    /// <summary>用当前用户的 DPAPI 密钥加密，返回 Base64 文本；失败返回 null。</summary>
    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;

        byte[] bytes;
        try { bytes = Encoding.UTF8.GetBytes(plain); }
        catch (Exception) { return null; }

        var input = default(DataBlob);
        var haveInput = false;
        try
        {
            input = AllocInput(bytes);
            haveInput = true;

            if (!CryptProtectData(ref input, Description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptprotectUiForbidden, out var output))
                return null;

            try { return Convert.ToBase64String(ReadAll(output)); }
            finally { ReleaseOutput(output); }
        }
        catch (Exception)
        {
            // P/Invoke 在极端情况下（如损坏的 DPAPI 主密钥）可能抛出包装异常，
            // 对调用方而言一律等价于「加密不可用」。
            return null;
        }
        finally
        {
            if (haveInput) Marshal.FreeHGlobal(input.PbData);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>解密 Base64 文本；密文损坏、换了用户或系统账户被重置时返回 null。</summary>
    public static string? Unprotect(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;

        byte[] cipher;
        try { cipher = Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }

        var input = default(DataBlob);
        var haveInput = false;
        try
        {
            input = AllocInput(cipher);
            haveInput = true;

            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptprotectUiForbidden, out var output))
                return null;

            try
            {
                var plainBytes = ReadAll(output);
                try { return Encoding.UTF8.GetString(plainBytes); }
                finally { CryptographicOperations.ZeroMemory(plainBytes); }
            }
            finally { ReleaseOutput(output); }
        }
        catch (Exception) { return null; }
        finally
        {
            if (haveInput) Marshal.FreeHGlobal(input.PbData);
            CryptographicOperations.ZeroMemory(cipher);
        }
    }

    private static DataBlob AllocInput(byte[] bytes)
    {
        // 长度为 0 时 AllocHGlobal(0) 返回的指针可能为 0，这里至少保证非空。
        var blob = new DataBlob
        {
            CbData = bytes.Length,
            PbData = Marshal.AllocHGlobal(Math.Max(1, bytes.Length))
        };
        if (bytes.Length > 0) Marshal.Copy(bytes, 0, blob.PbData, bytes.Length);
        return blob;
    }

    private static byte[] ReadAll(DataBlob blob)
    {
        if (blob.CbData <= 0) return Array.Empty<byte>();
        var result = new byte[blob.CbData];
        Marshal.Copy(blob.PbData, result, 0, blob.CbData);
        return result;
    }

    /// <summary>crypt32 分配的输出必须用 LocalFree 释放，不能用 FreeHGlobal。</summary>
    private static void ReleaseOutput(DataBlob blob)
    {
        if (blob.PbData != IntPtr.Zero) LocalFree(blob.PbData);
    }
}
