namespace Honua.Collect.Core.Field.Sensors;

/// <summary>
/// A deterministic, hardware-free <see cref="ISensorTransport"/> that replays a
/// scripted sequence of readings (BACKLOG I1/I3 test seam). It stands in for a
/// real Bluetooth/USB/NFC radio so the full ingestion pipeline — source, buffer,
/// aggregation, field binding — is exercisable in unit tests and demos.
/// </summary>
/// <remarks>
/// Replay is manual and synchronous (<see cref="Connect"/> then
/// <see cref="Emit(SensorReading)"/> / <see cref="EmitAll()"/>), so tests stay
/// deterministic with no timers or threads. Readings only flow while the
/// transport is <see cref="SensorConnectionState.Connected"/>.
/// </remarks>
public sealed class ReplaySensorTransport : ISensorTransport
{
    private readonly Queue<SensorReading> _scripted;
    private SensorConnectionState _state = SensorConnectionState.Disconnected;
    private bool _disposed;

    /// <summary>Creates a transport with an optional pre-scripted reading sequence.</summary>
    /// <param name="readings">Readings to replay in order via <see cref="EmitAll()"/>.</param>
    public ReplaySensorTransport(IEnumerable<SensorReading>? readings = null)
        => _scripted = new Queue<SensorReading>(readings ?? []);

    /// <inheritdoc />
    public SensorConnectionState State => _state;

    /// <inheritdoc />
    public event EventHandler<SensorConnectionState>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<SensorReading>? ReadingReceived;

    /// <summary>Number of scripted readings not yet emitted.</summary>
    public int Pending => _scripted.Count;

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Connect();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Disconnect();
        return Task.CompletedTask;
    }

    /// <summary>Synchronously transitions to <see cref="SensorConnectionState.Connected"/>.</summary>
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

    /// <summary>Synchronously transitions to <see cref="SensorConnectionState.Disconnected"/>.</summary>
    public void Disconnect() => SetState(SensorConnectionState.Disconnected);

    /// <summary>Simulates a link fault, transitioning to <see cref="SensorConnectionState.Faulted"/>.</summary>
    public void Fault() => SetState(SensorConnectionState.Faulted);

    /// <summary>
    /// Emits one reading to subscribers. No-op (the reading is dropped) when the
    /// transport is not <see cref="SensorConnectionState.Connected"/>, matching a
    /// real radio that delivers nothing while disconnected.
    /// </summary>
    /// <param name="reading">The reading to deliver.</param>
    /// <returns><see langword="true"/> if the reading was delivered.</returns>
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

    /// <summary>Emits all remaining scripted readings in order.</summary>
    /// <returns>The number of readings delivered.</returns>
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

    /// <inheritdoc />
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
