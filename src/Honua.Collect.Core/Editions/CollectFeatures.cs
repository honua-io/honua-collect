namespace Honua.Collect.Core.Editions;

/// <summary>
/// The open-core tier matrix, expressed as code: each gated capability mapped to
/// the minimum edition that unlocks it. This is the single source of truth the
/// README table mirrors.
/// </summary>
/// <remarks>
/// This type only models <em>which</em> edition unlocks <em>what</em>. It does
/// NOT verify entitlements — runtime enforcement (signed license keys, the
/// entitlement check ELv2 protects from circumvention) is a later layer. See
/// ROADMAP.md.
/// </remarks>
public static class CollectFeatures
{
    /// <summary>Minimum edition required to unlock each gated feature.</summary>
    public static IReadOnlyDictionary<CollectFeature, CollectEdition> MinimumEdition { get; } =
        new Dictionary<CollectFeature, CollectEdition>
        {
            [CollectFeature.ReportsAndExports] = CollectEdition.Pro,
            [CollectFeature.AiAssistedCapture] = CollectEdition.Pro,
            [CollectFeature.AdvancedSyncAndGis] = CollectEdition.Pro,
            [CollectFeature.EnterpriseAuthAndAdmin] = CollectEdition.Enterprise,
        };

    /// <summary>Whether <paramref name="edition"/> unlocks <paramref name="feature"/>.</summary>
    public static bool Includes(this CollectEdition edition, CollectFeature feature)
        => MinimumEdition.TryGetValue(feature, out var minimum) && edition >= minimum;
}
