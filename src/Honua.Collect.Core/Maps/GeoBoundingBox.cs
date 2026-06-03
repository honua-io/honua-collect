using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Maps;

/// <summary>
/// A geographic bounding box in decimal degrees, used to describe an offline
/// area to prefetch. Latitudes are clamped to the Web Mercator band.
/// </summary>
public sealed class GeoBoundingBox
{
    /// <summary>Creates a bounding box from its edges.</summary>
    /// <param name="south">The southern edge latitude (minimum).</param>
    /// <param name="west">The western edge longitude (minimum).</param>
    /// <param name="north">The northern edge latitude (maximum).</param>
    /// <param name="east">The eastern edge longitude (maximum).</param>
    /// <exception cref="ArgumentException">If south &gt; north or west &gt; east.</exception>
    public GeoBoundingBox(double south, double west, double north, double east)
    {
        if (south > north)
        {
            throw new ArgumentException("South edge must be <= north edge.", nameof(south));
        }

        if (west > east)
        {
            throw new ArgumentException("West edge must be <= east edge.", nameof(west));
        }

        South = south;
        West = west;
        North = north;
        East = east;
    }

    /// <summary>The southern (minimum latitude) edge in degrees.</summary>
    public double South { get; }

    /// <summary>The western (minimum longitude) edge in degrees.</summary>
    public double West { get; }

    /// <summary>The northern (maximum latitude) edge in degrees.</summary>
    public double North { get; }

    /// <summary>The eastern (maximum longitude) edge in degrees.</summary>
    public double East { get; }

    /// <summary>The geographic centre of the box.</summary>
    public FieldGeoPoint Center => new((South + North) / 2.0, (West + East) / 2.0);

    /// <summary>
    /// Builds a box from two opposite corners, in any order.
    /// </summary>
    /// <param name="a">One corner.</param>
    /// <param name="b">The opposite corner.</param>
    /// <returns>The normalised bounding box.</returns>
    public static GeoBoundingBox FromCorners(FieldGeoPoint a, FieldGeoPoint b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        return new GeoBoundingBox(
            Math.Min(a.Latitude, b.Latitude),
            Math.Min(a.Longitude, b.Longitude),
            Math.Max(a.Latitude, b.Latitude),
            Math.Max(a.Longitude, b.Longitude));
    }
}
