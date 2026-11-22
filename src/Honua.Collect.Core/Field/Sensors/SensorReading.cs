// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Field.Sensors;

public readonly record struct SensorReading(
    string SensorId,
    string Channel,
    double Value,
    string? Unit,
    DateTimeOffset TimestampUtc,
    SensorQuality Quality = SensorQuality.Good)
{
    public bool IsUsable => Quality != SensorQuality.Bad;

    public TimeSpan AgeAt(DateTimeOffset asOfUtc)
    {
        var age = asOfUtc - TimestampUtc;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }
}
