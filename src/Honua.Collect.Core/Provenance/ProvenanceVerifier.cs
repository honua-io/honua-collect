using Honua.Collect.Core.Licensing;

namespace Honua.Collect.Core.Provenance;

/// <summary>
/// Verifies signed capture provenance (BACKLOG, #41) — the independent check a
/// server, CLI, or reviewer runs to answer "was this taken by this user, at this
/// place and time, on this device, and unaltered?". Validates the Ed25519
/// signature over the manifest and, when the content is supplied, that the bytes
/// still hash to the manifest's digest.
/// </summary>
public static class ProvenanceVerifier
{
    /// <summary>
    /// Verifies the manifest signature against the signer's public key (the device
    /// or authority key). Does not inspect the content bytes.
    /// </summary>
    /// <param name="signed">The signed provenance.</param>
    /// <param name="publicKey">The raw 32-byte Ed25519 public key of the signer.</param>
    /// <returns>The verification result.</returns>
    public static ProvenanceVerification VerifySignature(SignedProvenance? signed, byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);

        if (signed?.Manifest is null || string.IsNullOrEmpty(signed.SignatureBase64))
        {
            return new ProvenanceVerification(ProvenanceStatus.Malformed);
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signed.SignatureBase64);
        }
        catch (FormatException)
        {
            return new ProvenanceVerification(ProvenanceStatus.Malformed);
        }

        var ok = Ed25519Signing.Verify(signed.Manifest.ToCanonicalBytes(), signature, publicKey);
        return new ProvenanceVerification(ok ? ProvenanceStatus.Valid : ProvenanceStatus.SignatureInvalid);
    }

    /// <summary>
    /// Fully verifies provenance: the signature must be valid <em>and</em> the supplied
    /// content must still hash to the manifest's digest. A valid signature over altered
    /// media is reported as <see cref="ProvenanceStatus.ContentMismatch"/>, distinct from
    /// a bad signature, so a verifier can tell "metadata tampered" from "media swapped".
    /// </summary>
    /// <param name="signed">The signed provenance.</param>
    /// <param name="content">The content bytes to check against the manifest.</param>
    /// <param name="publicKey">The raw 32-byte Ed25519 public key of the signer.</param>
    /// <returns>The verification result.</returns>
    public static ProvenanceVerification Verify(SignedProvenance? signed, byte[] content, byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(content);

        var signatureResult = VerifySignature(signed, publicKey);
        if (!signatureResult.IsValid)
        {
            return signatureResult;
        }

        return ContentHash.Matches(content, signed!.Manifest.ContentSha256)
            ? new ProvenanceVerification(ProvenanceStatus.Valid)
            : new ProvenanceVerification(ProvenanceStatus.ContentMismatch);
    }
}
