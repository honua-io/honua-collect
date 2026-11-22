// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Field.Measurement;

public readonly record struct PixelPoint(double X, double Y);

public sealed class ReferenceScale
{
    private ReferenceScale(double metersPerPixel) => MetersPerPixel = metersPerPixel;

    public double MetersPerPixel { get; }

    public static ReferenceScale FromReference(double referenceRealMeters, double referencePixelLength)
    {
        Require(referenceRealMeters, nameof(referenceRealMeters));
        Require(referencePixelLength, nameof(referencePixelLength));
        return new ReferenceScale(referenceRealMeters / referencePixelLength);
    }

    public double LengthMeters(double pixelLength)
    {
        if (pixelLength < 0 || double.IsNaN(pixelLength))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelLength), pixelLength, "Pixel length must be non-negative.");
        }

        return pixelLength * MetersPerPixel;
    }

    public double AreaSquareMeters(double pixelArea)
    {
        if (pixelArea < 0 || double.IsNaN(pixelArea))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelArea), pixelArea, "Pixel area must be non-negative.");
        }

        return pixelArea * MetersPerPixel * MetersPerPixel;
    }

    public double DistanceMeters(PixelPoint a, PixelPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return LengthMeters(Math.Sqrt((dx * dx) + (dy * dy)));
    }

    private static void Require(double value, string name)
    {
        if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "Reference measurements must be positive and finite.");
        }
    }
}
