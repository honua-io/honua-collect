// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Field.Measurement;

public sealed class CameraScale
{
    private CameraScale(double focalLengthPixels, double distanceMeters)
    {
        FocalLengthPixels = focalLengthPixels;
        DistanceMeters = distanceMeters;
    }

    public double FocalLengthPixels { get; }

    public double DistanceMeters { get; }

    public double MetersPerPixel => DistanceMeters / FocalLengthPixels;

    public static CameraScale FromFocalLengthPixels(double focalLengthPixels, double distanceMeters)
    {
        Require(focalLengthPixels, nameof(focalLengthPixels));
        Require(distanceMeters, nameof(distanceMeters));
        return new CameraScale(focalLengthPixels, distanceMeters);
    }

    public static CameraScale FromSensor(
        double focalLengthMillimeters,
        double sensorWidthMillimeters,
        double imageWidthPixels,
        double distanceMeters)
    {
        Require(focalLengthMillimeters, nameof(focalLengthMillimeters));
        Require(sensorWidthMillimeters, nameof(sensorWidthMillimeters));
        Require(imageWidthPixels, nameof(imageWidthPixels));
        var focalLengthPixels = focalLengthMillimeters * imageWidthPixels / sensorWidthMillimeters;
        return FromFocalLengthPixels(focalLengthPixels, distanceMeters);
    }

    public double LengthMeters(double pixelLength)
    {
        if (pixelLength < 0 || double.IsNaN(pixelLength))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelLength), pixelLength, "Pixel length must be non-negative.");
        }

        return pixelLength * MetersPerPixel;
    }

    public double DistanceBetween(PixelPoint a, PixelPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return LengthMeters(Math.Sqrt((dx * dx) + (dy * dy)));
    }

    private static void Require(double value, string name)
    {
        if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "Camera parameters must be positive and finite.");
        }
    }
}
