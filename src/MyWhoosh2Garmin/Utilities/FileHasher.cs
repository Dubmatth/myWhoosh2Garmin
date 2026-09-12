using System.Security.Cryptography;

namespace MyWhoosh2Garmin.Utilities;

public static class FileHasher
{
    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
