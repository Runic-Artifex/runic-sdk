using System;
using System.IO;
using System.Security.Cryptography;

namespace WebUIToolkit.DependencyNotices.Evidence;

public static class EvidenceDigest
{
    public const int Sha256HexLength = 64;

    public static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string ComputeSha256(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static bool IsCanonicalSha256(string? value)
    {
        if (value is null || value.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
