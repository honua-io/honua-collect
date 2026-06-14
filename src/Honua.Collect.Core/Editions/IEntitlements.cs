namespace Honua.Collect.Core.Editions;

/// <summary>
/// The runtime entitlement seam the product gates capabilities through: given the
/// edition (and any explicit feature grants) an instance is licensed for, answers
/// "may this capability be used?" and enforces it at feature entry points.
/// </summary>
/// <remarks>
/// Implemented by the static <see cref="CollectEntitlements"/> (edition-only) and
/// by the license-backed entitlements established from a verified signed key
/// (<c>Honua.Collect.Core.Licensing</c>). Depending on this interface — rather
/// than the concrete edition holder — lets the licensing layer become the trusted
/// source of entitlements without changing every gated call site.
/// </remarks>
public interface IEntitlements
{
    /// <summary>The edition in effect.</summary>
    CollectEdition Edition { get; }

    /// <summary>Whether the current entitlements unlock a feature.</summary>
    /// <param name="feature">Feature to check.</param>
    /// <returns><see langword="true"/> when the feature is available.</returns>
    bool Allows(CollectFeature feature);

    /// <summary>
    /// Enforces that a feature is available, throwing
    /// <see cref="FeatureNotEntitledException"/> when it is not.
    /// </summary>
    /// <param name="feature">Feature being used.</param>
    void Require(CollectFeature feature);
}
