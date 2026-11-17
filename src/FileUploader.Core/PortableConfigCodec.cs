using System.Security.Cryptography;

namespace FileUploader.Core;

/// <summary>
/// 导出配置的二进制封装：AES-256-GCM 加密，避免导出文件被记事本直接看到明文密钥。
/// <para>
/// 注意：密钥由程序内置常量派生，属于“防窥不防逆向”——能提高随手打开看一眼的门槛，
/// 但不能替代真正的密钥管理；需要强保护请配合访问控制 / 禁用此功能。
/// </para>
/// </summary>
internal static class PortableConfigCodec
{
    private static readonly byte[] Magic = "FUX1"u8.ToArray();

    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 4 + 1 + NonceSize + TagSize;

    // 程序内置密钥材料：同样的算法在别的机器上也能解开（这是便于分发的前提）。
    private static readonly byte[] Key = Rfc2898DeriveBytes.Pbkdf2(
        "FileUploader.PortableConfig.v1",
        "FileUploader/OSS/2026-export"u8.ToArray(),
        120_000,
        HashAlgorithmName.SHA256,
        32);

    /// <summary>是否是我们生成的二进制导出文件。</summary>
    public static bool LooksLikePortableConfig(ReadOnlySpan<byte> data) =>
        data.Length > HeaderSize && data[..4].SequenceEqual(Magic);

    /// <summary>明文 JSON → 二进制密文。</summary>
    public static byte[] Encrypt(byte[] plainJson)
    {
        var output = new byte[HeaderSize + plainJson.Length];
        Magic.CopyTo(output, 0);
        output[4] = Version;

        var nonce = output.AsSpan(5, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        var tag = output.AsSpan(5 + NonceSize, TagSize);
        var cipher = output.AsSpan(HeaderSize);

        using var aes = new AesGcm(Key, TagSize);
        aes.Encrypt(nonce, plainJson, cipher, tag);
        return output;
    }

    /// <summary>二进制密文 → 明文 JSON。</summary>
    public static byte[]? TryDecrypt(ReadOnlySpan<byte> data)
    {
        if (!LooksLikePortableConfig(data))
        {
            return null;
        }

        if (data[4] != Version)
        {
            return null;
        }

        var nonce = data.Slice(5, NonceSize);
        var tag = data.Slice(5 + NonceSize, TagSize);
        var cipher = data[HeaderSize..];
        var plain = new byte[cipher.Length];

        try
        {
            using var aes = new AesGcm(Key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
