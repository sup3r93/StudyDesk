using System.Security.Cryptography;
using StudyDesk.Server.Core.Security;
using Xunit;

namespace StudyDesk.Server.Tests.Security;

/// <summary>
/// Verifies the format, verifier derivation, and fail-closed behavior of device credentials.
/// </summary>
public sealed class DeviceTokenTests
{
    /// <summary>
    /// Ensures generated credentials use the required format and independently verify.
    /// </summary>
    [Fact]
    public void GenerateCreatesDistinctSixtyFourCharacterHexCredentials()
    {
        string firstToken = DeviceToken.Generate();
        string secondToken = DeviceToken.Generate();

        Assert.Matches("^[0-9A-Fa-f]{64}$", firstToken);
        Assert.Matches("^[0-9A-Fa-f]{64}$", secondToken);
        Assert.NotEqual(firstToken, secondToken);

        byte[] firstVerifier = DeviceToken.Hash(firstToken);
        byte[] secondVerifier = DeviceToken.Hash(secondToken);
        Assert.True(DeviceToken.Verify(firstToken, firstVerifier));
        Assert.True(DeviceToken.Verify(secondToken, secondVerifier));
        Assert.False(DeviceToken.Verify(firstToken, secondVerifier));
        Assert.False(DeviceToken.Verify(secondToken, firstVerifier));
    }

    /// <summary>
    /// Ensures the verifier hashes decoded token bytes and ignores hexadecimal letter case.
    /// </summary>
    [Fact]
    public void HashUsesDecodedBytesAndIsCaseInsensitive()
    {
        string token = DeviceToken.Generate();
        byte[] expected = SHA256.HashData(Convert.FromHexString(token));

        Assert.Equal(32, DeviceToken.Hash(token).Length);
        Assert.Equal(expected, DeviceToken.Hash(token));
        Assert.Equal(expected, DeviceToken.Hash(token.ToUpperInvariant()));
        Assert.Equal(expected, DeviceToken.Hash(token.ToLowerInvariant()));
        Assert.True(DeviceToken.Verify(token.ToUpperInvariant(), expected));
        Assert.True(DeviceToken.Verify(token.ToLowerInvariant(), expected));
    }

    /// <summary>
    /// Ensures malformed credentials cannot produce a verifier or authenticate.
    /// </summary>
    [Fact]
    public void InvalidTokenFormatsFailClosed()
    {
        string token = DeviceToken.Generate();
        byte[] verifier = DeviceToken.Hash(token);
        string?[] invalidTokens =
        [
            null,
            string.Empty,
            token[..63],
            token + "0",
            token[..63] + "G",
            " " + token[1..],
            token[..63] + " ",
            token + "\n",
            "\uFF10" + token[1..],
            "\u00E9" + token[1..]
        ];

        foreach (string? invalidToken in invalidTokens)
        {
            Assert.ThrowsAny<ArgumentException>(() => DeviceToken.Hash(invalidToken!));
            Assert.False(DeviceToken.Verify(invalidToken, verifier));
        }
    }

    /// <summary>
    /// Ensures only a matching verifier of the expected digest length authenticates.
    /// </summary>
    [Fact]
    public void VerifyRejectsWrongVerifierLengthsAndContent()
    {
        string token = DeviceToken.Generate();
        byte[] verifier = DeviceToken.Hash(token);
        byte[] wrongVerifier = (byte[])verifier.Clone();
        wrongVerifier[0] ^= 1;

        Assert.False(DeviceToken.Verify(token, Array.Empty<byte>()));
        Assert.False(DeviceToken.Verify(token, new byte[31]));
        Assert.False(DeviceToken.Verify(token, new byte[33]));
        Assert.False(DeviceToken.Verify(token, wrongVerifier));
    }

    /// <summary>
    /// Ensures possession of a stored verifier does not grant bearer-token access.
    /// </summary>
    [Fact]
    public void VerifierHexCannotAuthenticateAsTheOriginalToken()
    {
        string token = DeviceToken.Generate();
        byte[] verifier = DeviceToken.Hash(token);
        string verifierHex = Convert.ToHexString(verifier);

        Assert.False(DeviceToken.Verify(verifierHex, verifier));
    }
}
