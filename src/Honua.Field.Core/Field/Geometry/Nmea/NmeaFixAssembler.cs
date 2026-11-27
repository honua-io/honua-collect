// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Field.Core.Field.Geometry.Nmea;

/// <summary>
/// Assembles a live <see cref="NmeaFix"/> from a receiver's stream of NMEA
/// sentences, merging the complementary signals each one carries: GGA gives the
/// position, fix quality, HDOP, altitude and satellite count; GST refines the
/// horizontal accuracy estimate; RMC supplies the time and a validity flag. Feed
/// it sentences as they arrive and read <see cref="Current"/>. Talker id is
/// ignored, so GPS/GLONASS/Galileo/multi-constellation (GP/GL/GA/GN…) all work.
/// Call <see cref="Reset"/> when reconnecting a receiver or restarting a replay.
/// </summary>
public sealed class NmeaFixAssembler
{
    private NmeaFixQuality _quality = NmeaFixQuality.None;
    private double? _latitude;
    private double? _longitude;
    private double? _altitude;
    private double? _gstHorizontalAccuracy;
    private double? _verticalAccuracy;
    private double? _hdop;
    private double? _speed;
    private double? _course;
    private int? _satellites;
    private TimeSpan? _time;
    private TimeSpan? _lastTimedMeasurement;
    private DateOnly? _date;
    private bool _invalidEpoch;
    private (TimeSpan Time, double? Horizontal, double? Vertical)? _pendingGst;

    /// <summary>The fix assembled from sentences for the current UTC measurement time.</summary>
    public NmeaFix Current => new()
    {
        Quality = _quality,
        Latitude = _latitude,
        Longitude = _longitude,
        AltitudeMeters = _altitude,
        HorizontalAccuracyMeters = HorizontalAccuracy,
        VerticalAccuracyMeters = _verticalAccuracy,
        Hdop = _hdop,
        SpeedMetersPerSecond = _speed,
        CourseDegrees = _course,
        SatellitesUsed = _satellites,
        UtcTime = _time,
        Timestamp = BuildTimestamp(),
    };

    // Prefer the GST-measured horizontal accuracy; otherwise derive a coarse estimate
    // from HDOP so receivers that emit no GST still surface a usable accuracy figure.
    private double? HorizontalAccuracy => _gstHorizontalAccuracy
        ?? (_hdop is { } hdop ? hdop * NmeaFix.DefaultUereMeters : null);

    private DateTimeOffset? BuildTimestamp()
    {
        if (_date is { } date && _time is { } time)
        {
            return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) + time, TimeSpan.Zero);
        }

        return null;
    }

    /// <summary>Whether a usable positional fix has been assembled.</summary>
    public bool HasFix => Current.HasPosition;

    /// <summary>
    /// Processes one NMEA sentence, updating <see cref="Current"/>. Returns true when
    /// the sentence was checksum-valid and a recognized type (GGA/RMC/GST); false for
    /// a corrupt or unsupported sentence (which leaves the state unchanged).
    /// </summary>
    /// <param name="sentence">A raw NMEA sentence.</param>
    /// <returns>Whether the sentence was understood.</returns>
    public bool Process(string? sentence)
    {
        if (sentence is null)
        {
            return false;
        }

        var fields = NmeaParser.SplitFields(sentence);
        if (fields is null || fields.Length == 0 || fields[0].Length < 5)
        {
            return false;
        }

        // fields[0] is the address: a 2-char talker id + a 3-char sentence type.
        var type = fields[0].Substring(fields[0].Length - 3);
        return type switch
        {
            "GGA" => ProcessGga(fields),
            "RMC" => ProcessRmc(fields),
            "GST" => ProcessGst(fields),
            _ => false,
        };
    }

    /// <summary>Resets the assembler to begin a fresh fix.</summary>
    public void Reset()
    {
        ClearMeasurement();
        _time = _lastTimedMeasurement = null;
        _date = null;
        _invalidEpoch = false;
        _pendingGst = null;
    }

    private void ClearMeasurement()
    {
        _quality = NmeaFixQuality.None;
        _latitude = _longitude = _altitude = null;
        _gstHorizontalAccuracy = _verticalAccuracy = _hdop = _speed = _course = null;
        _satellites = null;
    }

    private bool TryBeginMeasurement(TimeSpan? time)
    {
        if (time is { } timed)
        {
            if (time == _time)
            {
                return true;
            }

            if (_lastTimedMeasurement is { } previous)
            {
                if (!IsLater(timed, previous))
                {
                    return false;
                }

                if (timed < previous && _date is { } date)
                {
                    _date = date.AddDays(1);
                }
            }

            _lastTimedMeasurement = timed;
        }

        ClearMeasurement();
        _time = time;
        _invalidEpoch = false;
        if (_pendingGst is { } pending)
        {
            if (pending.Time == time)
            {
                _gstHorizontalAccuracy = pending.Horizontal;
                _verticalAccuracy = pending.Vertical;
                _pendingGst = null;
            }
            else if (time is null || !IsLater(pending.Time, time.Value))
            {
                _pendingGst = null;
            }
        }

        return true;
    }

    // In a continuous receiver stream the closest time of day is the measurement
    // epoch. This handles the UTC midnight rollover in either arrival order.
    private static bool IsLater(TimeSpan candidate, TimeSpan previous)
    {
        var hours = (candidate - previous).TotalHours;
        return hours is > 0 and <= 12 or < -12;
    }

    private void InvalidateMeasurement()
    {
        ClearMeasurement();
        _invalidEpoch = true;
    }

    private bool ProcessGga(string[] f)
    {
        if (f.Length < 10)
        {
            return false;
        }

        if (!TryBeginMeasurement(NmeaParser.ParseTime(f[1])))
        {
            return true;
        }

        var quality = NmeaParser.ParseInt(f[6]) is { } q && Enum.IsDefined((NmeaFixQuality)q)
            ? (NmeaFixQuality)q
            : NmeaFixQuality.None;
        var latitude = NmeaParser.ParseLatitude(f[2], f[3]);
        var longitude = NmeaParser.ParseLongitude(f[4], f[5]);
        if (quality == NmeaFixQuality.None || latitude is null || longitude is null)
        {
            InvalidateMeasurement();
        }
        else if (!_invalidEpoch)
        {
            _quality = quality;
            _latitude = latitude;
            _longitude = longitude;
            _satellites = NmeaParser.ParseInt(f[7]);
            _hdop = ParseNonNegative(f[8]);
            _altitude = NmeaParser.ParseDouble(f[9]);
        }

        return true;
    }

    private bool ProcessRmc(string[] f)
    {
        if (f.Length < 7)
        {
            return false;
        }

        if (!TryBeginMeasurement(NmeaParser.ParseTime(f[1])))
        {
            return true;
        }

        _date = NmeaParser.ParseDate(f.Length > 9 ? f[9] : null) ?? _date;

        var latitude = NmeaParser.ParseLatitude(f[3], f[4]);
        var longitude = NmeaParser.ParseLongitude(f[5], f[6]);
        if (!string.Equals(f[2], "A", StringComparison.OrdinalIgnoreCase) ||
            latitude is null || longitude is null)
        {
            InvalidateMeasurement();
        }
        else if (!_invalidEpoch)
        {
            _speed = ParseNonNegative(f.Length > 7 ? f[7] : null) * KnotsToMetersPerSecond;
            _course = NmeaParser.ParseDouble(f.Length > 8 ? f[8] : null);
            if (_quality == NmeaFixQuality.None)
            {
                _latitude = latitude;
                _longitude = longitude;
                _quality = NmeaFixQuality.Autonomous;
            }
        }

        return true;
    }

    private bool ProcessGst(string[] f)
    {
        if (f.Length < 9)
        {
            return false;
        }

        if (NmeaParser.ParseTime(f[1]) is { } time)
        {
            var latStd = ParseNonNegative(f[6]);
            var lonStd = ParseNonNegative(f[7]);
            double? horizontal = latStd is { } la && lonStd is { } lo ? double.Hypot(la, lo) : null;
            var vertical = ParseNonNegative(f[8]);
            if (time == _time && !_invalidEpoch)
            {
                _gstHorizontalAccuracy = horizontal;
                _verticalAccuracy = vertical;
            }
            else if (time != _time &&
                (_lastTimedMeasurement is not { } previous || IsLater(time, previous)))
            {
                _pendingGst = (time, horizontal, vertical);
            }
        }

        return true;
    }

    private static double? ParseNonNegative(string? field)
        => NmeaParser.ParseDouble(field) is >= 0 and var value ? value : null;

    // 1 knot = 1 nautical mile (1852 m) per hour.
    private const double KnotsToMetersPerSecond = 1852.0 / 3600.0;
}
