namespace Honua.Collect.Core.Editions;

/// <summary>
/// Raised when a gated capability is used without the required edition.
/// </summary>
public sealed class FeatureNotEntitledException : InvalidOperationException
{
    /// <summary>Creates the exception for a denied feature.</summary>
    /// <param name="feature">The feature that was denied.</param>
    /// <param name="current">The current edition.</param>
    /// <param name="required">The edition the feature requires.</param>
    public FeatureNotEntitledException(CollectFeature feature, CollectEdition current, CollectEdition required)
        : base($"'{feature}' requires the {required} edition, but this instance is licensed for {current}.")
    {
        Feature = feature;
        CurrentEdition = current;
        RequiredEdition = required;
    }

    /// <summary>The feature that was denied.</summary>
    public CollectFeature Feature { get; }

    /// <summary>The edition in effect when the feature was denied.</summary>
    public CollectEdition CurrentEdition { get; }

    /// <summary>The minimum edition the feature requires.</summary>
    public CollectEdition RequiredEdition { get; }
}

/// <summary>
/// The runtime consumption side of the open-core tier matrix: given the edition
/// an instance is licensed for, answers "may this capability be used?" and
/// enforces it at feature entry points (export, AI capture, advanced sync,
/// enterprise admin).
/// </summary>
/// <remarks>
/// <para>
/// This is intentionally separate from <em>how the edition is established</em>.
/// Signed license-key verification — the anti-circumvention mechanism ELv2
/// protects — is the deferred upstream layer (see ROADMAP.md) and will be the
/// trusted source of the <see cref="Edition"/> value. This type is the seam the
/// rest of the product calls so that gating logic is centralised and testable
/// before that verification lands.
/// </para>
/// </remarks>
public sealed class CollectEntitlements : IEntitlements
{
    /// <summary>Creates an entitlement check for an edition.</summary>
    /// <param name="edition">The edition this instance is licensed for.</param>
    public CollectEntitlements(CollectEdition edition) => Edition = edition;

    /// <summary>The free Community baseline (no gated features).</summary>
    public static CollectEntitlements Community { get; } = new(CollectEdition.Community);

    /// <summary>The edition in effect.</summary>
    public CollectEdition Edition { get; }

    /// <summary>Whether the current edition unlocks a feature.</summary>
    /// <param name="feature">Feature to check.</param>
    /// <returns><see langword="true"/> when the feature is available.</returns>
    public bool Allows(CollectFeature feature) => Edition.Includes(feature);

    /// <summary>
    /// Enforces that a feature is available, throwing
    /// <see cref="FeatureNotEntitledException"/> when it is not. Call at the entry
    /// point of a gated capability.
    /// </summary>
    /// <param name="feature">Feature being used.</param>
    public void Require(CollectFeature feature)
    {
        if (!Allows(feature))
        {
            var required = CollectFeatures.MinimumEdition.TryGetValue(feature, out var min) ? min : CollectEdition.Enterprise;
            throw new FeatureNotEntitledException(feature, Edition, required);
        }
    }
}
