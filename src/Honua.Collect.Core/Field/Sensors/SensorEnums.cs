namespace Honua.Collect.Core.Field.Sensors;

/// <summary>Quality flag carried by a <see cref="SensorReading"/>.</summary>
public enum SensorQuality
{
    /// <summary>The reading is trustworthy.</summary>
    Good = 0,

    /// <summary>The reading is suspect (for example out of calibration range) but not invalid.</summary>
    Uncertain = 1,

    /// <summary>The reading is invalid and must not be bound to a field.</summary>
    Bad = 2,
}

/// <summary>
/// Connection state of an <see cref="ISensorSource"/> / its transport. The state
/// machine is intentionally small and platform-neutral; a real radio maps its
/// own lifecycle (scanning, pairing, GATT subscribe) onto these.
/// </summary>
public enum SensorConnectionState
{
    /// <summary>Not connected; no readings will arrive.</summary>
    Disconnected = 0,

    /// <summary>A connection attempt is in progress.</summary>
    Connecting = 1,

    /// <summary>Connected and streaming readings.</summary>
    Connected = 2,

    /// <summary>The connection faulted; the source may auto-retry to <see cref="Connecting"/>.</summary>
    Faulted = 3,
}

/// <summary>
/// Broad family of a sensor, so the UI and bindings can reason about a source
/// without knowing the concrete device. Open-ended on purpose — the wire decoder
/// owns the precise model.
/// </summary>
public enum SensorType
{
    /// <summary>Unknown / unspecified.</summary>
    Unknown = 0,

    /// <summary>Environmental probe (temperature, humidity, pressure, gas).</summary>
    Environmental = 1,

    /// <summary>External GNSS / positioning receiver (see also the NMEA pipeline).</summary>
    Positioning = 2,

    /// <summary>Laser / ultrasonic rangefinder or distance meter.</summary>
    Rangefinder = 3,

    /// <summary>Radiation, sound-level, or other physical-quantity meter.</summary>
    Meter = 4,

    /// <summary>Generic industrial / IoT telemetry source.</summary>
    Industrial = 5,
}
