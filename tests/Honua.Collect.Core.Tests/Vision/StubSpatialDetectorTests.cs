using Honua.Collect.Core.Vision;

namespace Honua.Collect.Core.Tests.Vision;

public class StubSpatialDetectorTests
{
    private static readonly byte[] AnyBytes = [1, 2, 3];

    private static StubSpatialDetector Detector() => new(
        [
            new Detection("utility-pole", 0.92, new NormalizedBoundingBox(0.10, 0.20, 0.05, 0.40)),
            new Detection("utility-pole", 0.61, new NormalizedBoundingBox(0.50, 0.20, 0.05, 0.40)),
            new Detection("corrosion", 0.40, new NormalizedBoundingBox(0.30, 0.30, 0.10, 0.10)),
        ],
        imageWidth: 4000,
        imageHeight: 3000);

    [Fact]
    public async Task Returns_all_scripted_detections_and_image_size_by_default()
    {
        var result = await Detector().DetectAsync(AnyBytes);

        Assert.Equal(3, result.Detections.Count);
        Assert.Equal(4000, result.ImageWidth);
        Assert.Equal(3000, result.ImageHeight);
    }

    [Fact]
    public async Task Is_deterministic_regardless_of_image_bytes()
    {
        var detector = Detector();
        var a = await detector.DetectAsync(new byte[] { 9, 9, 9 });
        var b = await detector.DetectAsync(new byte[] { 0 });

        Assert.Equal(a.Detections, b.Detections);
    }

    [Fact]
    public async Task Honours_min_confidence_floor()
    {
        var result = await Detector().DetectAsync(AnyBytes, new SpatialDetectionOptions { MinConfidence = 0.6 });

        Assert.Equal(2, result.Detections.Count);
        Assert.All(result.Detections, d => Assert.True(d.Confidence >= 0.6));
    }

    [Fact]
    public async Task Honours_label_allow_list_case_insensitively()
    {
        var result = await Detector().DetectAsync(
            AnyBytes, new SpatialDetectionOptions { Labels = ["CORROSION"] });

        Assert.Single(result.Detections);
        Assert.Equal("corrosion", result.Detections[0].Label);
    }

    [Fact]
    public async Task Honours_max_detections_cap()
    {
        var result = await Detector().DetectAsync(AnyBytes, new SpatialDetectionOptions { MaxDetections = 1 });

        Assert.Single(result.Detections);
        Assert.Equal("utility-pole", result.Detections[0].Label);
    }

    [Fact]
    public async Task Cancellation_is_observed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Detector().DetectAsync(AnyBytes, cancellationToken: cts.Token));
    }

    [Fact]
    public void Rejects_out_of_unit_square_boxes_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StubSpatialDetector(
            [new Detection("x", 0.9, new NormalizedBoundingBox(0.9, 0.0, 0.5, 0.1))]));
    }

    [Fact]
    public void Reports_a_model_id()
    {
        Assert.Equal("stub", Detector().ModelId);
        Assert.Equal("yolo-test", new StubSpatialDetector([], modelId: "yolo-test").ModelId);
    }
}
