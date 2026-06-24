using System.Collections.Concurrent;

namespace Honua.Collect.Core.Field.Sensors;

/// <summary>
/// The default <see cref="ISensorSource"/>: it subscribes to an
/// <see cref="ISensorTransport"/>, fans incoming readings into a per-channel
/// <see cref="SensorReadingBuffer"/>, and re-publishes state and readings. This
/// is the platform-neutral glue between the device radio and the aggregation /
/// field-binding steps; only the transport is hardware-bound.
/// </summary>
public sealed class SensorSource : ISensorSource, IAsyncDisposable
{
    private readonly ISensorTransport _transport;
    private readonly ConcurrentDictionary<string, SensorReadingBuffer> _buffers;
    private readonly int _windowCapacity;
    private readonly BufferOverflowPolicy _overflowPolicy;
    private bool _disposed;

    /// <summary>Creates a source over a transport.</summary>
    /// <param name="sensorId">Stable sensor identifier.</param>
    /// <param name="type">Sensor family.</param>
    /// <param name="transport">The device-radio transport feeding readings.</param>
    /// <param name="windowCapacity">Per-channel rolling-window capacity (&gt; 0).</param>
    /// <param name="overflowPolicy">Per-channel buffer overflow policy.</param>
    public SensorSource(
        string sensorId,
        SensorType type,
        ISensorTransport transport,
        int windowCapacity = 64,
        BufferOverflowPolicy overflowPolicy = BufferOverflowPolicy.DropOldest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sensorId);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowCapacity, 1);

        SensorId = sensorId;
        Type = type;
        _transport = transport;
        _windowCapacity = windowCapacity;
        _overflowPolicy = overflowPolicy;
        _buffers = new ConcurrentDictionary<string, SensorReadingBuffer>(StringComparer.OrdinalIgnoreCase);

        _transport.StateChanged += OnTransportStateChanged;
        _transport.ReadingReceived += OnTransportReading;
    }

    /// <inheritdoc />
    public string SensorId { get; }

    /// <inheritdoc />
    public SensorType Type { get; }

    /// <inheritdoc />
    public SensorConnectionState State => _transport.State;

    /// <inheritdoc />
    public event EventHandler<SensorConnectionState>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<SensorReading>? ReadingReceived;

    /// <inheritdoc />
    public IReadOnlyCollection<string> Channels => _buffers.Keys.ToArray();

    /// <summary>Opens the underlying transport.</summary>
    /// <param name="cancellationToken">Cancels the connect.</param>
    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => _transport.ConnectAsync(cancellationToken);

    /// <summary>Closes the underlying transport.</summary>
    /// <param name="cancellationToken">Cancels the disconnect.</param>
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => _transport.DisconnectAsync(cancellationToken);

    /// <inheritdoc />
    public SensorReading? Latest(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        return _buffers.TryGetValue(channel, out var buffer) ? buffer.Latest : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<SensorReading> Window(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        return _buffers.TryGetValue(channel, out var buffer) ? buffer.Snapshot() : [];
    }

    /// <summary>Gets (creating if needed) the buffer for a channel.</summary>
    /// <param name="channel">Channel name.</param>
    /// <returns>The channel's buffer.</returns>
    public SensorReadingBuffer BufferFor(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        return _buffers.GetOrAdd(channel, _ => new SensorReadingBuffer(_windowCapacity, _overflowPolicy));
    }

    private void OnTransportReading(object? sender, SensorReading reading)
    {
        BufferFor(reading.Channel).Add(reading);
        ReadingReceived?.Invoke(this, reading);
    }

    private void OnTransportStateChanged(object? sender, SensorConnectionState state)
        => StateChanged?.Invoke(this, state);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transport.StateChanged -= OnTransportStateChanged;
        _transport.ReadingReceived -= OnTransportReading;
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
