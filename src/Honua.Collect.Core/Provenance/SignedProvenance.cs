namespace Honua.Collect.Core.Provenance;

/// <summary>A capture provenance manifest together with its Ed25519 signature.</summary>
/// <param name="Manifest">The provenance manifest.</param>
/// <param name="SignatureBase64">Base64 Ed25519 signature over <see cref="CaptureProvenance.ToCanonicalBytes"/>.</param>
public sealed record SignedProvenance(CaptureProvenance Manifest, string SignatureBase64);

/// <summary>Outcome of verifying a signed provenance manifest.</summary>
public enum ProvenanceStatus
{
    /// <summary>Signature valid and (when checked) the content matches its hash.</summary>
    Valid,

    /// <summary>The signature does not match the manifest under the authority/device key — untrusted or altered metadata.</summary>
    SignatureInvalid,

    /// <summary>Signature valid, but the supplied content does not match the manifest's hash — the media was altered.</summary>
    ContentMismatch,

    /// <summary>The manifest or signature was structurally unusable.</summary>
    Malformed,
}

/// <summary>The result of verifying signed provenance.</summary>
/// <param name="Status">The verification outcome.</param>
public sealed record ProvenanceVerification(ProvenanceStatus Status)
{
    /// <summary>Whether the provenance is fully verified.</summary>
    public bool IsValid => Status == ProvenanceStatus.Valid;
}
