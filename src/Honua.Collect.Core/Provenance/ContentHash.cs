using System.Security.Cryptography;

namespace Honua.Collect.Core.Provenance;

/// <summary>
/// Content hashing for capture provenance: a SHA-256 digest binds a photo (or any
/// captured bytes) to its signed manifest, so any later edit to the media is
/// detectable independently of the signature.
/// </summary>
public static class ContentHash
{
    /// <summary>Computes the lowercase-hex SHA-256 digest of a byte buffer.</summary>
    /// <param name="content">The captured bytes (e.g. an image).</param>
    /// <returns>A 64-character lowercase hex digest.</returns>
    public static string Sha256Hex(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    /// <summary>Whether a buffer matches an expected lowercase-hex SHA-256 digest (case-insensitive).</summary>
    /// <param name="content">The bytes to check.</param>
    /// <param name="expectedHex">The expected hex digest.</param>
    /// <returns><see langword="true"/> when the digests match.</returns>
    public static bool Matches(byte[] content, string? expectedHex)
        => !string.IsNullOrEmpty(expectedHex)
           && string.Equals(Sha256Hex(content), expectedHex, StringComparison.OrdinalIgnoreCase);
}
