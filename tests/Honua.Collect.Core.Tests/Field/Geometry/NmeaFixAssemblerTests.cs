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

        // No accuracy estimate: accepted unless the caller requires one.
        var noGst = new NmeaFixAssembler();
        noGst.Process(Gga);
        Assert.True(noGst.Current.MeetsAccuracy(0.05));
        Assert.False(noGst.Current.MeetsAccuracy(0.05, requireAccuracy: true));

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
}
