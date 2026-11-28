using System.Security.Cryptography;

namespace StudyDesk.Server.Core.Security;

/// <summary>Creates and verifies high-entropy device credentials without retaining cleartext.</summary>
public static class DeviceToken
{
    private const int SecretByteCount = 32;

    /// <summary>Generates one independent 256-bit credential as 64 lowercase hexadecimal characters.</summary>
    /// <returns>The cleartext credential, which must only be stored in protected client storage.</returns>
    public static string Generate()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(SecretByteCount);
        try
        {
            return Convert.ToHexStringLower(secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Hashes the decoded credential bytes for server-side verifier storage.</summary>
    /// <param name="token">Exactly 64 ASCII hexadecimal characters.</param>
    /// <returns>A 32-byte SHA-256 verifier, not a bearer credential.</returns>
    /// <exception cref="ArgumentException">The credential format is invalid.</exception>
    public static byte[] Hash(string token)
    {
        if (!HasValidFormat(token))
        {
            throw new ArgumentException("The device credential must contain exactly 64 ASCII hexadecimal characters.", nameof(token));
        }

        byte[] secret = Convert.FromHexString(token);
        try
        {
            return SHA256.HashData(secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Checks a raw credential against a stored verifier and fails closed on invalid input.</summary>
    /// <param name="token">The raw client credential; it may be absent.</param>
    /// <param name="expectedVerifier">The stored 32-byte digest.</param>
    /// <returns>Whether the credential has the required format and matches the verifier.</returns>
    public static bool Verify(string? token, ReadOnlySpan<byte> expectedVerifier)
    {
        if (expectedVerifier.Length != SecretByteCount || !HasValidFormat(token))
        {
            return false;
        }

        byte[] actualVerifier = Hash(token!);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actualVerifier, expectedVerifier);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualVerifier);
        }
    }

    /// <summary>Checks the narrow credential grammar without trimming or Unicode normalization.</summary>
    private static bool HasValidFormat(string? token)
    {
        if (token is null || token.Length != SecretByteCount * 2)
        {
            return false;
        }

        foreach (char character in token)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f') and not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }
}
