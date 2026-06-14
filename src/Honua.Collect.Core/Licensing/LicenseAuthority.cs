namespace Honua.Collect.Core.Licensing;

/// <summary>
/// The licensing authority's public key, embedded in the shipping app to verify
/// license keys fully offline. The matching <em>private</em> key is held offline by
/// the licensing authority and is never present in this repository or the binary —
/// only signatures it produces can be trusted, which is the anti-circumvention
/// property ELv2 backs.
/// </summary>
/// <remarks>
/// Provision the production key per release. To rotate: generate a new key pair
/// (<see cref="Ed25519Signing.GenerateKeyPair"/>), publish the public key here, and
/// re-issue licenses. The default below is a development key; releases override it.
/// </remarks>
public static class LicenseAuthority
{
    /// <summary>Raw 32-byte Ed25519 public key (base64) used to verify license keys.</summary>
    public const string PublicKeyBase64 = "mRPUb1rJ5yx8PHaHzIdroUDTF+1KmPFPFBwacFYL29Y=";

    /// <summary>The embedded public key as raw bytes.</summary>
    public static byte[] PublicKey { get; } = Convert.FromBase64String(PublicKeyBase64);
}
