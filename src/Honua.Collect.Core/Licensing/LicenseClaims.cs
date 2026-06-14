using Honua.Collect.Core.Editions;

namespace Honua.Collect.Core.Licensing;

/// <summary>
/// The verified contents of a Honua Collect license key: the tier it grants, any
/// explicitly entitled add-on features, who it was issued to, and its validity
/// window. This is the trusted source of the running edition once the signature
/// has been checked against the authority public key — the layer the open-core
/// tier matrix (<see cref="CollectFeatures"/>) is gated through.
/// </summary>
public sealed record LicenseClaims
{
    /// <summary>The edition (tier) the license grants.</summary>
    public required CollectEdition Edition { get; init; }

    /// <summary>
    /// Features entitled explicitly, on top of whatever the <see cref="Edition"/>
    /// already includes. Lets an add-on grant a single capability without bumping
    /// the whole tier; empty for a plain tier license.
    /// </summary>
    public IReadOnlySet<CollectFeature> Features { get; init; } = new HashSet<CollectFeature>();

    /// <summary>The licensed customer / organization (for display and support).</summary>
    public required string Customer { get; init; }

    /// <summary>Unique license id (for revocation lists and support).</summary>
    public required string LicenseId { get; init; }

    /// <summary>When the license was issued (UTC).</summary>
    public required DateTimeOffset IssuedAtUtc { get; init; }

    /// <summary>When the license expires (UTC). A perpetual license may set this far in the future.</summary>
    public required DateTimeOffset ExpiresAtUtc { get; init; }

    /// <summary>Whether this is a time-limited trial (drives trial UX and downgrade-on-expiry).</summary>
    public bool IsTrial { get; init; }

    /// <summary>Whether the license has expired as of the given time.</summary>
    /// <param name="asOfUtc">Reference time.</param>
    /// <returns><see langword="true"/> when expired.</returns>
    public bool IsExpired(DateTimeOffset asOfUtc) => asOfUtc >= ExpiresAtUtc;

    /// <summary>Whether the validity window itself is coherent (issued before it expires).</summary>
    public bool HasCoherentWindow => ExpiresAtUtc > IssuedAtUtc;
}
