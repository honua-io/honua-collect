namespace Honua.Collect.Core.Licensing;

/// <summary>Outcome of verifying a license key.</summary>
public enum LicenseStatus
{
    /// <summary>Signature valid, well-formed, and within its validity window.</summary>
    Valid,

    /// <summary>Signature valid but the license has expired.</summary>
    Expired,

    /// <summary>Signature valid but the license is not yet active (issued in the future).</summary>
    NotYetValid,

    /// <summary>The signature does not match the authority key — untrusted or tampered.</summary>
    InvalidSignature,

    /// <summary>The token could not be parsed, or its claims are incoherent.</summary>
    Malformed,
}

/// <summary>The result of verifying a license key.</summary>
/// <param name="Status">The verification outcome.</param>
/// <param name="Claims">The decoded claims when the signature verified (even if expired / not-yet-valid); null when malformed or untrusted.</param>
public sealed record LicenseVerificationResult(LicenseStatus Status, LicenseClaims? Claims)
{
    /// <summary>Whether the license is valid and usable right now.</summary>
    public bool IsValid => Status == LicenseStatus.Valid;

    /// <summary>A malformed/unparseable result with no claims.</summary>
    public static LicenseVerificationResult Malformed { get; } = new(LicenseStatus.Malformed, null);

    /// <summary>An untrusted (bad-signature) result with no claims.</summary>
    public static LicenseVerificationResult Untrusted { get; } = new(LicenseStatus.InvalidSignature, null);
}
