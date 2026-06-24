using Honua.Collect.Core.Field.Geometry;
using Honua.Collect.Core.Field.Geometry.Nmea;

namespace Honua.Collect.Core.Tests.Field.Geometry;

public class NmeaFixAssemblerTests
{
    private const string Gga = "$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47";
    private const string Gst = "$GPGST,172814.0,0.006,0.023,0.020,273.6,0.023,0.020,0.031*6A";

    // Builds a checksum-valid sentence from its body (an independent XOR, so it
    // cross-checks NmeaParser.ValidateChecksum rather than reusing it).
    private static string Nmea(string body)
    {
        byte cs = 0;
        foreach (var c in body)
        {
            cs ^= (byte)c;
        }

        return $"${body}*{cs:X2}";
    }

    [Fact]
    public void Gga_establishes_position_quality_and_dop()
    {
        var asm = new NmeaFixAssembler();

        Assert.True(asm.Process(Gga));
        var fix = asm.Current;

        Assert.True(fix.HasPosition);
        Assert.Equal(NmeaFixQuality.Autonomous, fix.Quality);
        Assert.Equal(48.1173, fix.Latitude!.Value, 4);
        Assert.Equal(11.51667, fix.Longitude!.Value, 4);
        Assert.Equal(0.9, fix.Hdop!.Value, 3);
        Assert.Equal(8, fix.SatellitesUsed);
        Assert.Equal(545.4, fix.AltitudeMeters!.Value, 3);
        Assert.Equal(new TimeSpan(12, 35, 19), fix.UtcTime);
    }

    [Fact]
    public void Gst_refines_horizontal_accuracy()
    {
        var asm = new NmeaFixAssembler();
        asm.Process(Gga);

        asm.Process(Gst);

        // sqrt(0.023^2 + 0.020^2) ~= 0.0305 m
        Assert.Equal(Math.Sqrt((0.023 * 0.023) + (0.020 * 0.020)), asm.Current.HorizontalAccuracyMeters!.Value, 6);
    }

    [Theory]
    [InlineData(4, NmeaFixQuality.RtkFixed, true)]
    [InlineData(5, NmeaFixQuality.RtkFloat, false)]
    [InlineData(2, NmeaFixQuality.Differential, false)]
    public void Gga_quality_indicator_maps_to_fix_type(int indicator, NmeaFixQuality expected, bool isRtkFixed)
    {
        var asm = new NmeaFixAssembler();
        asm.Process(Nmea($"GNGGA,140730.00,4807.0380,N,01131.0000,E,{indicator},12,0.6,560.0,M,46.9,M,1.0,0000"));

        Assert.Equal(expected, asm.Current.Quality);
        Assert.Equal(isRtkFixed, asm.Current.IsRtkFixed);
        Assert.True(asm.Current.HasPosition);
    }

    [Fact]
    public void A_no_fix_gga_clears_the_position()
    {
        var asm = new NmeaFixAssembler();
        asm.Process(Gga);
        Assert.True(asm.HasFix);

        asm.Process(Nmea("GPGGA,123519,,,,,0,00,,,M,,M,,")); // quality 0

        Assert.False(asm.HasFix);
        Assert.Equal(NmeaFixQuality.None, asm.Current.Quality);
        Assert.Null(asm.Current.Latitude);
    }

    [Fact]
    public void Rmc_supplies_a_position_when_valid_and_nothing_when_void()
    {
        var valid = new NmeaFixAssembler();
        valid.Process(Nmea("GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W"));
        Assert.True(valid.HasFix);
        Assert.Equal(NmeaFixQuality.Autonomous, valid.Current.Quality);
        Assert.Equal(48.1173, valid.Current.Latitude!.Value, 4);

        var void_ = new NmeaFixAssembler();
        void_.Process(Nmea("GPRMC,123519,V,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W"));
        Assert.False(void_.HasFix);
    }

    [Fact]
    public void Corrupt_or_unsupported_sentences_are_ignored()
    {
        var asm = new NmeaFixAssembler();
        asm.Process(Gga);
        var before = asm.Current;

        Assert.False(asm.Process("$GPGGA,corrupt*00")); // bad checksum
        Assert.False(asm.Process(Nmea("GPGSV,3,1,11,01,40,083,46"))); // unsupported type
        Assert.False(asm.Process(null));

        Assert.Equal(before, asm.Current); // unchanged
    }

    [Fact]
    public void Reset_clears_the_assembled_fix()
    {
        var asm = new NmeaFixAssembler();
        asm.Process(Gga);
        asm.Process(Gst);

        asm.Reset();

        Assert.False(asm.HasFix);
        Assert.Equal(NmeaFixQuality.None, asm.Current.Quality);
        Assert.Null(asm.Current.HorizontalAccuracyMeters);
    }

    [Fact]
    public void MeetsAccuracy_gates_on_position_and_estimate()
    {
        var asm = new NmeaFixAssembler();
        asm.Process(Gga);
        asm.Process(Gst); // ~0.03 m

        var fix = asm.Current;
        Assert.True(fix.MeetsAccuracy(0.05));
        Assert.False(fix.MeetsAccuracy(0.01));

        // No accuracy estimate at all (empty HDOP, no GST): accepted unless required.
        var noEstimate = new NmeaFixAssembler();
        noEstimate.Process(Nmea("GPGGA,123519,4807.038,N,01131.000,E,1,08,,545.4,M,46.9,M,,"));
        Assert.Null(noEstimate.Current.HorizontalAccuracyMeters);
        Assert.True(noEstimate.Current.MeetsAccuracy(0.05));
        Assert.False(noEstimate.Current.MeetsAccuracy(0.05, requireAccuracy: true));

        // No position at all is never acceptable.
        Assert.False(new NmeaFixAssembler().Current.MeetsAccuracy(1000));
    }

    [Fact]
    public void ToFieldGeoPoint_carries_coordinate_and_accuracy_or_throws()
    {
        var asm = new NmeaFixAssembler();
        asm.Process(Gga);
        asm.Process(Gst);

        var point = asm.Current.ToFieldGeoPoint();
        Assert.Equal(48.1173, point.Latitude, 4);
        Assert.Equal(11.51667, point.Longitude, 4);
        Assert.NotNull(point.AccuracyMeters);

        Assert.Throws<InvalidOperationException>(() => new NmeaFixAssembler().Current.ToFieldGeoPoint());
    }

    [Fact]
    public void Without_gst_horizontal_accuracy_falls_back_to_hdop()
    {
        var asm = new NmeaFixAssembler();
        asm.Process(Gga); // HDOP 0.9, no GST

        // HDOP * default UERE (5 m) ≈ 4.5 m.
        Assert.Equal(0.9 * NmeaFix.DefaultUereMeters, asm.Current.HorizontalAccuracyMeters!.Value, 6);
    }

    [Fact]
    public void Gst_horizontal_accuracy_takes_precedence_over_hdop_fallback()
    {
        var asm = new NmeaFixAssembler();
        asm.Process(Gga); // would give ~4.5 m from HDOP
        asm.Process(Gst); // ~0.03 m measured

        // The measured GST value wins, not the HDOP fallback.
        Assert.Equal(Math.Sqrt((0.023 * 0.023) + (0.020 * 0.020)), asm.Current.HorizontalAccuracyMeters!.Value, 6);
    }

    [Fact]
    public void Gst_supplies_vertical_accuracy()
    {
        var asm = new NmeaFixAssembler();
        asm.Process(Gga);
        asm.Process(Gst); // altitude std dev field = 0.031

        Assert.Equal(0.031, asm.Current.VerticalAccuracyMeters!.Value, 6);
    }

    [Fact]
    public void Rmc_supplies_speed_course_and_full_timestamp()
    {
        var asm = new NmeaFixAssembler();
        asm.Process(Nmea("GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W"));
        var fix = asm.Current;

        // 22.4 knots -> m/s.
        Assert.Equal(22.4 * (1852.0 / 3600.0), fix.SpeedMetersPerSecond!.Value, 6);
        Assert.Equal(84.4, fix.CourseDegrees!.Value, 3);
        Assert.Equal(new DateTimeOffset(1994, 3, 23, 12, 35, 19, TimeSpan.Zero), fix.Timestamp);
    }

    [Fact]
    public void Timestamp_is_null_until_both_date_and_time_are_known()
    {
        // GGA alone gives a time-of-day but no date.
        var ggaOnly = new NmeaFixAssembler();
        ggaOnly.Process(Gga);
        Assert.NotNull(ggaOnly.Current.UtcTime);
        Assert.Null(ggaOnly.Current.Timestamp);

        // GGA (time + position) then RMC (date) yields a full timestamp.
        var combined = new NmeaFixAssembler();
        combined.Process(Gga);
        combined.Process(Nmea("GNRMC,123519,A,4807.038,N,01131.000,E,000.0,000.0,230394,,,A"));
        Assert.Equal(new DateTimeOffset(1994, 3, 23, 12, 35, 19, TimeSpan.Zero), combined.Current.Timestamp);
    }

    [Theory]
    [InlineData("GPGGA")] // GPS
    [InlineData("GNGGA")] // multi-constellation
    [InlineData("GLGGA")] // GLONASS talker id
    public void Talker_id_is_ignored_for_gga(string address)
    {
        var asm = new NmeaFixAssembler();
        Assert.True(asm.Process(Nmea($"{address},123519,4807.038,N,01131.000,E,4,12,0.6,545.4,M,46.9,M,,")));
        Assert.True(asm.Current.IsRtkFixed);
        Assert.True(asm.Current.HasPosition);
    }

    [Fact]
    public void Rtk_float_gga_maps_to_float_quality()
    {
        var asm = new NmeaFixAssembler();
        asm.Process(Nmea("GNGGA,140730.00,4807.0380,N,01131.0000,E,5,12,0.6,560.0,M,46.9,M,1.0,0000"));

        Assert.Equal(NmeaFixQuality.RtkFloat, asm.Current.Quality);
        Assert.False(asm.Current.IsRtkFixed);
    }

    [Fact]
    public void Garbled_partial_lines_are_ignored_and_leave_state_intact()
    {
        var asm = new NmeaFixAssembler();
        asm.Process(Gga);
        var before = asm.Current;

        Assert.False(asm.Process("$GNGGA,123519,4807")); // truncated, no checksum
        Assert.False(asm.Process("garbage"));
        Assert.False(asm.Process("$*00"));
        Assert.False(asm.Process(Nmea("GNGGA,123519"))); // valid checksum but too few fields

        Assert.Equal(before, asm.Current);
    }

    [Fact]
    public void Parsed_fixes_feed_the_gps_averager()
    {
        // The whole point of the parser is to drive capture: parsed fixes flow into
        // the existing GpsAverager seam without any new averaging code.
        var averager = new GpsAverager();
        var asm = new NmeaFixAssembler();

        asm.Process(Nmea("GPGGA,123519,4807.0380,N,01131.0000,E,4,12,0.6,545.4,M,46.9,M,,"));
        asm.Process(Gst);
        averager.Add(asm.Current.ToFieldGeoPoint());

        asm.Process(Nmea("GPGGA,123520,4807.0382,N,01131.0002,E,4,12,0.6,545.4,M,46.9,M,,"));
        averager.Add(asm.Current.ToFieldGeoPoint());

        Assert.Equal(2, averager.SampleCount);
        var avg = averager.Average();
        Assert.Equal(48.1173, avg.Latitude, 3);
        Assert.NotNull(avg.AccuracyMeters);
    }
}
