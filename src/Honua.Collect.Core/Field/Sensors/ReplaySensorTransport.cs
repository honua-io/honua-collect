// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.Core.Field.Sensors;

public sealed class ReplaySensorTransport : ISensorTransport
{
    private readonly Queue<SensorReading> _scripted;
    private SensorConnectionState _state = SensorConnectionState.Disconnected;
    private bool _disposed;

    public ReplaySensorTransport(IEnumerable<SensorReading>? readings = null)
        => _scripted = new Queue<SensorReading>(readings ?? []);

    public SensorConnectionState State => _state;

    public event EventHandler<SensorConnectionState>? StateChanged;

    public event EventHandler<SensorReading>? ReadingReceived;

    public int Pending => _scripted.Count;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Connect();
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Disconnect();
        return Task.CompletedTask;
    }

    public void Connect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state == SensorConnectionState.Connected)
        {
            return;
        }

        SetState(SensorConnectionState.Connecting);
        SetState(SensorConnectionState.Connected);
    }

    public void Disconnect() => SetState(SensorConnectionState.Disconnected);

    public void Fault() => SetState(SensorConnectionState.Faulted);

    public bool Emit(SensorReading reading)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != SensorConnectionState.Connected)
        {
            return false;
        }

        ReadingReceived?.Invoke(this, reading);
        return true;
    }

    public int EmitAll()
    {
        var delivered = 0;
        while (_scripted.Count > 0)
        {
            if (Emit(_scripted.Dequeue()))
            {
                delivered++;
            }
        }

        return delivered;
    }

    private void SetState(SensorConnectionState next)
    {
        if (_state == next)
        {
            return;
        }

        _state = next;
        StateChanged?.Invoke(this, next);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            SetState(SensorConnectionState.Disconnected);
        }

        return ValueTask.CompletedTask;
    }
}
