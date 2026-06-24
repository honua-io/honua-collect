using Honua.Collect.Core.Field.Geometry.Nmea;

namespace Honua.Collect.Core.Tests.Field.Geometry;

public class NmeaParserTests
{
    // Real, documented NMEA sentences with correct checksums.
    private const string Gga = "$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47";
    private const string Rmc = "$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W*6A";
    private const string Gst = "$GPGST,172814.0,0.006,0.023,0.020,273.6,0.023,0.020,0.031*6A";

    [Theory]
    [InlineData(Gga)]
    [InlineData(Rmc)]
    [InlineData(Gst)]
    public void ValidateChecksum_accepts_valid_sentences(string sentence)
    {
        Assert.True(NmeaParser.ValidateChecksum(sentence));
        Assert.True(NmeaParser.ValidateChecksum(sentence + "\r\n")); // trailing newline tolerated
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-dollar")]
    [InlineData("$GPGGA,123519,4807.038,N*ZZ")] // non-hex checksum
    [InlineData("$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*48")] // wrong checksum (47 flipped)
    public void ValidateChecksum_rejects_invalid_sentences(string? sentence)
    {
        Assert.False(NmeaParser.ValidateChecksum(sentence));
    }

    [Fact]
    public void ParseLatitude_converts_ddmm_to_decimal_degrees()
    {
        Assert.Equal(48.1173, NmeaParser.ParseLatitude("4807.038", "N")!.Value, 4);
        Assert.Equal(-48.1173, NmeaParser.ParseLatitude("4807.038", "S")!.Value, 4);
    }

    [Fact]
    public void ParseLongitude_converts_dddmm_to_decimal_degrees()
    {
        Assert.Equal(11.51667, NmeaParser.ParseLongitude("01131.000", "E")!.Value, 4);
        Assert.Equal(-11.51667, NmeaParser.ParseLongitude("01131.000", "W")!.Value, 4);
    }

    [Theory]
    [InlineData("", "N")]
    [InlineData("4807.038", "")]
    [InlineData("4807.038", "X")] // unknown hemisphere
    [InlineData("4860.000", "N")] // minutes >= 60
    [InlineData("not-a-number", "N")]
    public void ParseCoordinate_rejects_bad_input(string value, string hemisphere)
    {
        Assert.Null(NmeaParser.ParseLatitude(value, hemisphere));
    }

    [Fact]
    public void ParseTime_reads_hhmmss_with_optional_fraction()
    {
        Assert.Equal(new TimeSpan(12, 35, 19), NmeaParser.ParseTime("123519"));
        Assert.Equal(new TimeSpan(0, 17, 28, 14), NmeaParser.ParseTime("172814.0"));
        Assert.Null(NmeaParser.ParseTime("bad"));
        Assert.Null(NmeaParser.ParseTime("993519")); // hour out of range
    }

    [Fact]
    public void ParseDate_reads_ddmmyy_with_pivoted_two_digit_year()
    {
        Assert.Equal(new DateOnly(1994, 3, 23), NmeaParser.ParseDate("230394")); // 94 -> 1994
        Assert.Equal(new DateOnly(2024, 12, 31), NmeaParser.ParseDate("311224")); // 24 -> 2024
        Assert.Equal(new DateOnly(1970, 1, 1), NmeaParser.ParseDate("010170")); // 70 pivot -> 1970
        Assert.Equal(new DateOnly(2069, 1, 1), NmeaParser.ParseDate("010169")); // 69 pivot -> 2069
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2303")]    // too short
    [InlineData("23039400")] // too long
    [InlineData("321394")]  // day out of range
    [InlineData("231394")]  // month out of range
    [InlineData("290224")]  // Feb 29 exists in 2024 (leap) — sanity that this is accepted below
    [InlineData("bad394")]
    public void ParseDate_rejects_bad_input_except_valid_leap_day(string? value)
    {
        // 290224 is a valid leap day; everything else here is invalid.
        if (value == "290224")
        {
            Assert.Equal(new DateOnly(2024, 2, 29), NmeaParser.ParseDate(value));
            return;
        }

        Assert.Null(NmeaParser.ParseDate(value));
    }

    [Fact]
    public void ParseDate_rejects_nonexistent_leap_day()
    {
        Assert.Null(NmeaParser.ParseDate("290223")); // Feb 29 2023 does not exist
    }
}
