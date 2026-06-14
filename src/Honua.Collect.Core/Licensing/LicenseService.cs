using Honua.Collect.Core.Editions;

namespace Honua.Collect.Core.Licensing;

/// <summary>
/// Establishes the running entitlements from a signed license key and owns the
/// edition lifecycle: apply a key (activation / upgrade), drop to the free
/// Community baseline when none is valid (no key, tampered, or expired
/// downgrade), and re-evaluate as the clock crosses a trial's expiry. Verification
/// is offline against the embedded authority public key; an invalid or expired key
/// never faults the app — gated features simply lock.
/// </summary>
public sealed class LicenseService
{
    private readonly byte[] _publicKey;
    private readonly TimeProvider _clock;
    private string? _token;

    /// <summary>Creates the service over the authority public key.</summary>
    /// <param name="publicKey">Authority public key; defaults to the embedded production key.</param>
    /// <param name="clock">Time source (defaults to system); injectable for tests.</param>
    public LicenseService(byte[]? publicKey = null, TimeProvider? clock = null)
    {
        _publicKey = publicKey ?? LicenseAuthority.PublicKey;
        _clock = clock ?? TimeProvider.System;
        Entitlements = LicenseEntitlements.Community;
        Status = LicenseStatus.Malformed;
    }

    /// <summary>Raised when the effective entitlements change (activation, upgrade, or downgrade).</summary>
    public event EventHandler? Changed;

    /// <summary>The entitlements currently in effect (never null; Community when unlicensed).</summary>
    public IEntitlements Entitlements { get; private set; }

    /// <summary>The status of the last applied key (drives trial / expired UX).</summary>
    public LicenseStatus Status { get; private set; }

    /// <summary>The verified claims currently in effect, or null when running on the Community baseline.</summary>
    public LicenseClaims? ActiveLicense =>
        Entitlements is LicenseEntitlements { IsLicensed: true } licensed ? licensed.License : null;

    /// <summary>
    /// Applies a license key string (activation or upgrade). A valid key establishes
    /// its edition; anything else (null, tampered, malformed, expired, not-yet-valid)
    /// leaves the app on the Community baseline. Returns the resulting status.
    /// </summary>
    /// <param name="token">The signed license key, or null to clear to Community.</param>
    /// <returns>The verification status of the applied key.</returns>
    public LicenseStatus Apply(string? token)
    {
        _token = token;
        return Reevaluate();
    }

    /// <summary>Clears any applied license, dropping to the Community baseline.</summary>
    public void Clear() => Apply(null);

    /// <summary>
    /// Re-evaluates the currently applied key against the present time — call when a
    /// trial may have crossed its expiry — and downgrades to Community if it has.
    /// </summary>
    /// <returns>The current verification status.</returns>
    public LicenseStatus Refresh() => Reevaluate();

    private LicenseStatus Reevaluate()
    {
        var result = LicenseKey.Verify(_token, _publicKey, _clock.GetUtcNow());
        var next = result.IsValid && result.Claims is not null
            ? LicenseEntitlements.FromClaims(result.Claims)
            : LicenseEntitlements.Community;

        var prev = Entitlements;
        Entitlements = next;
        Status = result.Status;

        if (prev.Edition != next.Edition || IsLicensed(prev) != IsLicensed(next))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return Status;
    }

    private static bool IsLicensed(IEntitlements entitlements)
        => entitlements is LicenseEntitlements { IsLicensed: true };
}
