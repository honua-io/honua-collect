using Honua.Collect.Core.Field.Sensors;

namespace Honua.Collect.Core.Tests.Field.Sensors;

public class SensorIngestionTests
{
    private static SensorReading R(double value, string unit = "degC", int secondsAgo = 0, SensorQuality q = SensorQuality.Good)
        => new("probe-1", "temperature", value, unit, Origin.AddSeconds(-secondsAgo), q);

    private static readonly DateTimeOffset Origin = new(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);

    // ---- Buffer: bounded + rolling window ---------------------------------

    [Fact]
    public void Buffer_keeps_only_the_most_recent_readings_within_capacity()
    {
        var buffer = new SensorReadingBuffer(capacity: 3);

        for (var i = 1; i <= 5; i++)
        {
            buffer.Add(R(i));
        }

        Assert.Equal(3, buffer.Count);
        Assert.Equal([3, 4, 5], buffer.Snapshot().Select(r => r.Value));
        Assert.Equal(5, buffer.Latest!.Value.Value);
        Assert.Equal(2, buffer.DroppedCount); // two oldest evicted
    }

    [Fact]
    public void Buffer_backpressure_rejects_new_readings_when_full_under_DropNewest()
    {
        var buffer = new SensorReadingBuffer(capacity: 2, BufferOverflowPolicy.DropNewest);

        Assert.True(buffer.Add(R(1)));
        Assert.True(buffer.Add(R(2)));
        Assert.False(buffer.Add(R(3))); // rejected — window full
        Assert.False(buffer.Add(R(4)));

        Assert.Equal([1, 2], buffer.Snapshot().Select(r => r.Value));
        Assert.Equal(2, buffer.Latest!.Value.Value); // latest unchanged on rejection
        Assert.Equal(2, buffer.DroppedCount);
    }

    [Fact]
    public void Buffer_rejects_nonpositive_capacity()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SensorReadingBuffer(0));

    // ---- Aggregation: latest / avg / min / max ----------------------------

    [Fact]
    public void Aggregation_computes_latest_average_min_max()
    {
        // Newest reading is the 30 (secondsAgo=0); oldest is 10.
        var window = new[] { R(10, secondsAgo: 4), R(20, secondsAgo: 2), R(30, secondsAgo: 0) };

        Assert.Equal(30, SensorAggregator.Aggregate(window, SensorAggregation.Latest)!.Value.Value);
        Assert.Equal(20, SensorAggregator.Aggregate(window, SensorAggregation.Average)!.Value.Value);
        Assert.Equal(10, SensorAggregator.Aggregate(window, SensorAggregation.Minimum)!.Value.Value);
        Assert.Equal(30, SensorAggregator.Aggregate(window, SensorAggregation.Maximum)!.Value.Value);
    }

    [Fact]
    public void Aggregation_excludes_bad_readings_and_reports_worst_quality()
    {
        var window = new[]
        {
            R(10, secondsAgo: 2),
            R(999, secondsAgo: 1, q: SensorQuality.Bad), // excluded entirely
            R(20, secondsAgo: 0, q: SensorQuality.Uncertain),
        };

        var avg = SensorAggregator.Aggregate(window, SensorAggregation.Average)!.Value;
        Assert.Equal(15, avg.Value); // (10+20)/2, the 999 dropped
        Assert.Equal(2, avg.SampleCount);
        Assert.Equal(SensorQuality.Uncertain, avg.Quality);
    }

    [Fact]
    public void Aggregation_of_empty_or_all_bad_window_is_null()
    {
        Assert.Null(SensorAggregator.Aggregate([], SensorAggregation.Latest));
        Assert.Null(SensorAggregator.Aggregate([R(1, q: SensorQuality.Bad)], SensorAggregation.Latest));
    }

    // ---- Source over the fake transport -----------------------------------

    [Fact]
    public async Task Replay_transport_feeds_source_buffers_per_channel()
    {
        await using var transport = new ReplaySensorTransport(
        [
            new SensorReading("env", "temperature", 21.0, "degC", Origin, SensorQuality.Good),
            new SensorReading("env", "humidity", 55.0, "%", Origin, SensorQuality.Good),
            new SensorReading("env", "temperature", 22.0, "degC", Origin, SensorQuality.Good),
        ]);
        await using var source = new SensorSource("env", SensorType.Environmental, transport, windowCapacity: 8);

        var received = 0;
        source.ReadingReceived += (_, _) => received++;

        await source.ConnectAsync();
        transport.EmitAll();

        Assert.Equal(3, received);
        Assert.Equal(["temperature", "humidity"], source.Channels.OrderByDescending(c => c).ToArray());
        Assert.Equal(22.0, source.Latest("temperature")!.Value.Value);
        Assert.Equal(2, source.Window("temperature").Count);
        Assert.Single(source.Window("humidity"));
        Assert.Empty(source.Window("pressure")); // unknown channel
    }

    [Fact]
    public async Task Disconnected_transport_drops_readings()
    {
        await using var transport = new ReplaySensorTransport();
        await using var source = new SensorSource("env", SensorType.Environmental, transport);

        // Not connected yet — emit is a no-op.
        Assert.False(transport.Emit(R(1)));
        Assert.Empty(source.Channels);

        await source.ConnectAsync();
        Assert.True(transport.Emit(R(2)));
        Assert.Single(source.Channels);
    }
}
