// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace Honua.Collect.Core.Field.Sensors;

public sealed class SensorSource : ISensorSource, IAsyncDisposable
{
    private readonly ISensorTransport _transport;
    private readonly ConcurrentDictionary<string, SensorReadingBuffer> _buffers;
    private readonly int _windowCapacity;
    private readonly BufferOverflowPolicy _overflowPolicy;
    private bool _disposed;

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

    public string SensorId { get; }

    public SensorType Type { get; }

    public SensorConnectionState State => _transport.State;

    public event EventHandler<SensorConnectionState>? StateChanged;

    public event EventHandler<SensorReading>? ReadingReceived;

    public IReadOnlyCollection<string> Channels => _buffers.Keys.ToArray();

    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => _transport.ConnectAsync(cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => _transport.DisconnectAsync(cancellationToken);

    public SensorReading? Latest(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        return _buffers.TryGetValue(channel, out var buffer) ? buffer.Latest : null;
    }

    public IReadOnlyList<SensorReading> Window(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        return _buffers.TryGetValue(channel, out var buffer) ? buffer.Snapshot() : [];
    }

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
