// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Field.Geometry;

public sealed class GpsAverager
{
    private double _sumLat;
    private double _sumLon;
    private double _sumAccuracy;
    private int _accuracySamples;

    public int SampleCount { get; private set; }

    public bool HasSamples => SampleCount > 0;

    public void Add(FieldGeoPoint fix)
    {
        ArgumentNullException.ThrowIfNull(fix);

        _sumLat += fix.Latitude;
        _sumLon += fix.Longitude;
        SampleCount++;

        if (fix.AccuracyMeters is { } accuracy)
        {
            _sumAccuracy += accuracy;
            _accuracySamples++;
        }
    }

    public FieldGeoPoint Average()
    {
        if (SampleCount == 0)
        {
            throw new InvalidOperationException("No GPS samples have been collected.");
        }

        double? accuracy = _accuracySamples > 0
            ? _sumAccuracy / _accuracySamples / Math.Sqrt(_accuracySamples)
            : null;

        return new FieldGeoPoint(_sumLat / SampleCount, _sumLon / SampleCount, accuracy);
    }

    public void Reset()
    {
        _sumLat = 0;
        _sumLon = 0;
        _sumAccuracy = 0;
        _accuracySamples = 0;
        SampleCount = 0;
    }
}
