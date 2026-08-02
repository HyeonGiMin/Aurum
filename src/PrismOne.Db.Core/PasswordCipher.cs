using System.Security.Cryptography;

namespace PrismOne.Db.Core;

/// <summary>
/// 저장되는 비밀번호의 AES-256-GCM 암호화.
/// 키는 ~/.prismone-studio/key.bin (소유자만 읽기 가능, 0600) 에 보관한다.
/// 키 파일이 없으면 생성하므로 사용자는 아무것도 관리할 필요 없다.
/// </summary>
public static class PasswordCipher
{
    private const string Prefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".prismone-studio");

    private static string KeyPath => Path.Combine(Dir, "key.bin");

    private static byte[] GetKey()
    {
        if (File.Exists(KeyPath))
            return File.ReadAllBytes(KeyPath);

        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Dir);
        File.WriteAllBytes(KeyPath, key);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }

    public static string Protect(string plain)
    {
        if (plain.Length == 0) return "";
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(GetKey(), TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        var packed = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, NonceSize);
        cipher.CopyTo(packed, NonceSize + TagSize);
        return Prefix + Convert.ToBase64String(packed);
    }

    /// <summary>복호화. 접두사 없는 값은 예전 평문 저장분으로 보고 그대로 돌려준다 (다음 저장 때 암호화됨).</summary>
    public static string? Unprotect(string? stored)
    {
        if (stored is null || !stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;
        try
        {
            var packed = Convert.FromBase64String(stored[Prefix.Length..]);
            var nonce = packed.AsSpan(0, NonceSize);
            var tag = packed.AsSpan(NonceSize, TagSize);
            var cipher = packed.AsSpan(NonceSize + TagSize);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(GetKey(), TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;   // 키가 바뀌었거나 손상 — 비밀번호만 다시 입력받으면 된다
        }
    }
}
