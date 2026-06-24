namespace Honua.Collect.Core.Field.Sensors;

/// <summary>
/// A logical sensor the capture flow binds to (BACKLOG I1/I3): a stable identity
/// and type, a live connection state, and a stream of <see cref="SensorReading"/>s
/// over one or more channels. A source sits above an <see cref="ISensorTransport"/>
/// (the device radio) and buffers per-channel readings for aggregation and field
/// binding.
/// </summary>
public interface ISensorSource
{
    /// <summary>Stable identifier of the sensor.</summary>
    string SensorId { get; }

    /// <summary>The sensor's broad family.</summary>
    SensorType Type { get; }

    /// <summary>The current connection state of the underlying transport.</summary>
    SensorConnectionState State { get; }

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    event EventHandler<SensorConnectionState>? StateChanged;

    /// <summary>Raised for every reading the source ingests.</summary>
    event EventHandler<SensorReading>? ReadingReceived;

    /// <summary>The channels seen so far on this source.</summary>
    IReadOnlyCollection<string> Channels { get; }

    /// <summary>Gets the latest reading for a channel, or <see langword="null"/> if none.</summary>
    /// <param name="channel">Channel name.</param>
    SensorReading? Latest(string channel);

    /// <summary>Takes a snapshot of the rolling window for a channel.</summary>
    /// <param name="channel">Channel name.</param>
    /// <returns>The buffered readings (oldest to newest); empty when the channel is unknown.</returns>
    IReadOnlyList<SensorReading> Window(string channel);
}
