namespace Honua.Collect.Core.Field.Geometry.Nmea;

/// <summary>
/// Assembles a live <see cref="NmeaFix"/> from a receiver's stream of NMEA
/// sentences, merging the complementary signals each one carries: GGA gives the
/// position, fix quality, HDOP, altitude and satellite count; GST refines the
/// horizontal accuracy estimate; RMC supplies the time and a validity flag. Feed
/// it sentences as they arrive and read <see cref="Current"/>. Talker id is
/// ignored, so GPS/GLONASS/Galileo/multi-constellation (GP/GL/GA/GN…) all work.
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
    private DateOnly? _date;

    /// <summary>The fix assembled from everything seen so far.</summary>
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
        _quality = NmeaFixQuality.None;
        _latitude = _longitude = _altitude = null;
        _gstHorizontalAccuracy = _verticalAccuracy = _hdop = _speed = _course = null;
        _satellites = null;
        _time = null;
        _date = null;
    }

    private bool ProcessGga(string[] f)
    {
        if (f.Length < 10)
        {
            return false;
        }

        _time = NmeaParser.ParseTime(f[1]) ?? _time;
        _quality = NmeaParser.ParseInt(f[6]) is { } q && Enum.IsDefined((NmeaFixQuality)q)
            ? (NmeaFixQuality)q
            : NmeaFixQuality.None;
        _satellites = NmeaParser.ParseInt(f[7]) ?? _satellites;
        _hdop = NmeaParser.ParseDouble(f[8]) ?? _hdop;
        _altitude = NmeaParser.ParseDouble(f[9]) ?? _altitude;

        if (_quality != NmeaFixQuality.None)
        {
            _latitude = NmeaParser.ParseLatitude(f[2], f[3]) ?? _latitude;
            _longitude = NmeaParser.ParseLongitude(f[4], f[5]) ?? _longitude;
        }
        else
        {
            // No fix: drop any stale coordinate so HasPosition reflects reality.
            _latitude = null;
            _longitude = null;
        }

        return true;
    }

    private bool ProcessRmc(string[] f)
    {
        if (f.Length < 7)
        {
            return false;
        }

        _time = NmeaParser.ParseTime(f[1]) ?? _time;

        // Speed (knots, f[7]), course (degrees true, f[8]) and date (ddmmyy, f[9]) are
        // optional fields — capture them whenever present, independent of fix validity.
        if (NmeaParser.ParseDouble(f.Length > 7 ? f[7] : null) is { } knots)
        {
            _speed = knots * KnotsToMetersPerSecond;
        }

        _course = NmeaParser.ParseDouble(f.Length > 8 ? f[8] : null) ?? _course;
        _date = NmeaParser.ParseDate(f.Length > 9 ? f[9] : null) ?? _date;

        var valid = string.Equals(f[2], "A", StringComparison.OrdinalIgnoreCase);
        if (valid)
        {
            var lat = NmeaParser.ParseLatitude(f[3], f[4]);
            var lon = NmeaParser.ParseLongitude(f[5], f[6]);
            if (lat is not null && lon is not null)
            {
                _latitude = lat;
                _longitude = lon;
                // RMC carries no quality indicator; a valid sentence is at least an
                // autonomous 2D fix, so don't leave the position quality at None.
                if (_quality == NmeaFixQuality.None)
                {
                    _quality = NmeaFixQuality.Autonomous;
                }
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

        var latStd = NmeaParser.ParseDouble(f[6]);
        var lonStd = NmeaParser.ParseDouble(f[7]);
        if (latStd is { } la && lonStd is { } lo)
        {
            _gstHorizontalAccuracy = Math.Sqrt((la * la) + (lo * lo));
        }

        _verticalAccuracy = NmeaParser.ParseDouble(f[8]) ?? _verticalAccuracy;

        return true;
    }

    // 1 knot = 1 nautical mile (1852 m) per hour.
    private const double KnotsToMetersPerSecond = 1852.0 / 3600.0;
}
