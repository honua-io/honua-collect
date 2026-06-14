using Honua.Collect.Core.Editions;

namespace Honua.Collect.Core.Licensing;

/// <summary>
/// Entitlements established from a verified license: the edition's matrix grants
/// plus any features the license entitles explicitly. Falls back to the free
/// Community baseline when no valid license is in effect, so the app degrades
/// gracefully (gated features lock) rather than failing.
/// </summary>
public sealed class LicenseEntitlements : IEntitlements
{
    private readonly IReadOnlySet<CollectFeature> _explicitFeatures;

    /// <summary>Creates entitlements for a verified edition + explicit feature grants.</summary>
    /// <param name="edition">The licensed edition.</param>
    /// <param name="explicitFeatures">Features granted on top of the edition, if any.</param>
    /// <param name="license">The verified claims backing these entitlements, when licensed.</param>
    public LicenseEntitlements(
        CollectEdition edition,
        IReadOnlySet<CollectFeature>? explicitFeatures = null,
        LicenseClaims? license = null)
    {
        Edition = edition;
        _explicitFeatures = explicitFeatures ?? new HashSet<CollectFeature>();
        License = license;
    }

    /// <summary>The free Community baseline (no license, no gated features).</summary>
    public static LicenseEntitlements Community { get; } = new(CollectEdition.Community);

    /// <summary>The verified license backing these entitlements, or null when unlicensed (Community).</summary>
    public LicenseClaims? License { get; }

    /// <summary>Whether a valid license is in effect (false for the Community fallback).</summary>
    public bool IsLicensed => License is not null;

    /// <inheritdoc />
    public CollectEdition Edition { get; }

    /// <summary>Builds entitlements directly from verified claims.</summary>
    /// <param name="claims">The verified license claims.</param>
    /// <returns>License-backed entitlements.</returns>
    public static LicenseEntitlements FromClaims(LicenseClaims claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        return new LicenseEntitlements(claims.Edition, claims.Features, claims);
    }

    /// <inheritdoc />
    public bool Allows(CollectFeature feature)
        => Edition.Includes(feature) || _explicitFeatures.Contains(feature);

    /// <inheritdoc />
    public void Require(CollectFeature feature)
    {
        if (!Allows(feature))
        {
            var required = CollectFeatures.MinimumEdition.TryGetValue(feature, out var min)
                ? min
                : CollectEdition.Enterprise;
            throw new FeatureNotEntitledException(feature, Edition, required);
        }
    }
}
