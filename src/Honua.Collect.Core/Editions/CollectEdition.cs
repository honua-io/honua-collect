namespace Honua.Collect.Core.Editions;

/// <summary>
/// The licensing edition a running instance of Honua Collect is entitled to.
/// Ordered so that a higher edition is a superset of the ones below it.
/// </summary>
public enum CollectEdition
{
    /// <summary>Free, fully usable baseline.</summary>
    Community = 0,

    /// <summary>Adds reports/exports, AI-assisted capture, advanced sync &amp; GIS.</summary>
    Pro = 1,

    /// <summary>Adds enterprise auth &amp; admin on top of Pro.</summary>
    Enterprise = 2,
}
