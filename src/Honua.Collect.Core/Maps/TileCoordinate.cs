namespace Honua.Collect.Core.Maps;

/// <summary>An XYZ slippy-map tile address: a zoom level and tile column/row.</summary>
/// <param name="Zoom">The tile zoom level (0 = whole world in one tile).</param>
/// <param name="X">The tile column.</param>
/// <param name="Y">The tile row.</param>
public readonly record struct TileCoordinate(int Zoom, int X, int Y)
{
    /// <summary>The canonical <c>{z}/{x}/{y}</c> key for this tile.</summary>
    public override string ToString() => $"{Zoom}/{X}/{Y}";
}
