namespace Honua.Collect.Core.Field.Sensors;

/// <summary>
/// The device-radio seam (BACKLOG I1): the part a platform layer implements with
/// a real Bluetooth, USB-serial, or NFC stack. The transport's only job is to
/// open/close a link and surface decoded <see cref="SensorReading"/>s plus its
/// connection state; everything above it (buffering, aggregation, field binding)
/// is platform-neutral and lives in Core.
/// </summary>
/// <remarks>
/// This mirrors the NMEA/GNSS shape (PR #77): the platform-bound transport is a
/// thin "frames in, readings out" feed, and the verifiable value is the Core
/// pipeline it drives. A deterministic <see cref="ReplaySensorTransport"/> ships
/// for tests so the whole pipeline is exercisable without hardware.
/// </remarks>
public interface ISensorTransport : IAsyncDisposable
{
    /// <summary>The current link state.</summary>
    SensorConnectionState State { get; }

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    event EventHandler<SensorConnectionState>? StateChanged;

    /// <summary>Raised for each reading the transport decodes off the wire.</summary>
    event EventHandler<SensorReading>? ReadingReceived;

    /// <summary>Opens the link and begins streaming. Idempotent.</summary>
    /// <param name="cancellationToken">Cancels the connect attempt.</param>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the link and stops streaming. Idempotent.</summary>
    /// <param name="cancellationToken">Cancels the disconnect.</param>
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
