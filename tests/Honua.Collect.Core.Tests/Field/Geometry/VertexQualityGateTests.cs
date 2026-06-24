using Honua.Collect.Core.Field.Geometry;
using Honua.Collect.Core.Field.Geometry.Nmea;
using Honua.Sdk.Field.Records;

namespace Honua.Collect.Core.Tests.Field.Geometry;

public class VertexQualityGateTests
{
    private static GpsAverager AveragerWith(int samples, double? accuracy)
    {
        var averager = new GpsAverager();
        for (var i = 0; i < samples; i++)
        {
            averager.Add(new FieldGeoPoint(47.0, 8.0, accuracy));
        }

        return averager;
    }

    [Fact]
    public void Rejects_when_too_few_samples()
    {
        var gate = new VertexQualityGate { MinimumSamples = 5 };
        var result = gate.Evaluate(AveragerWith(2, 1.0));

        Assert.False(result.Accepted);
        Assert.Equal(VertexQualityRejection.TooFewSamples, result.Rejection);
        Assert.Equal(2, result.SampleCount);
    }

    [Fact]
    public void Accepts_a_settled_average_within_accuracy()
    {
        var gate = new VertexQualityGate { MinimumSamples = 4, MaxAccuracyMeters = 1.0 };
        // Four 1.5 m samples → mean 1.5 reduced by √4 = 0.75 m, under the 1 m bar.
        var result = gate.Evaluate(AveragerWith(4, 1.5));

        Assert.True(result.Accepted);
        Assert.Equal(VertexQualityRejection.None, result.Rejection);
        Assert.NotNull(result.Point);
    }

    [Fact]
    public void Rejects_when_averaged_accuracy_exceeds_the_maximum()
    {
        var gate = new VertexQualityGate { MaxAccuracyMeters = 0.5 };
        // Single 2 m sample → √1 reduction leaves 2 m, over the 0.5 m bar.
        var result = gate.Evaluate(AveragerWith(1, 2.0));

        Assert.False(result.Accepted);
        Assert.Equal(VertexQualityRejection.AveragedAccuracyTooLow, result.Rejection);
    }

    [Fact]
    public void Rejects_when_the_live_fix_has_degraded()
    {
        var gate = new VertexQualityGate { MaxAccuracyMeters = 0.05 };
        var degraded = new NmeaFix
        {
            Quality = NmeaFixQuality.Autonomous,
            Latitude = 47.0,
            Longitude = 8.0,
            HorizontalAccuracyMeters = 1.2, // metre-grade, fails the 5 cm bar
        };

        var result = gate.Evaluate(AveragerWith(10, 0.02), degraded);

        Assert.False(result.Accepted);
        Assert.Equal(VertexQualityRejection.FixBelowAccuracy, result.Rejection);
    }

    [Fact]
    public void Accepts_when_both_average_and_live_rtk_fix_are_good()
    {
        var gate = new VertexQualityGate { MinimumSamples = 5, MaxAccuracyMeters = 0.05 };
        var rtk = new NmeaFix
        {
            Quality = NmeaFixQuality.RtkFixed,
            Latitude = 47.0,
            Longitude = 8.0,
            HorizontalAccuracyMeters = 0.014,
        };

        var result = gate.Evaluate(AveragerWith(9, 0.03), rtk);

        Assert.True(result.Accepted);
        Assert.Equal(VertexQualityRejection.None, result.Rejection);
        Assert.Equal(9, result.SampleCount);
    }

    [Fact]
    public void Require_fix_accuracy_rejects_a_fix_with_no_estimate()
    {
        var gate = new VertexQualityGate { MaxAccuracyMeters = 1.0, RequireFixAccuracy = true };
        var noEstimate = new NmeaFix
        {
            Quality = NmeaFixQuality.Autonomous,
            Latitude = 47.0,
            Longitude = 8.0,
            // No HorizontalAccuracyMeters reported.
        };

        var result = gate.Evaluate(AveragerWith(3, 0.2), noEstimate);

        Assert.False(result.Accepted);
        Assert.Equal(VertexQualityRejection.FixBelowAccuracy, result.Rejection);
    }
}
