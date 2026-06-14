namespace Honua.Collect.Core.Field.Geometry.Nmea;

/// <summary>
/// GNSS fix quality, as reported by the NMEA GGA sentence's quality indicator.
/// Surfacing this is what lets a survey crew see they are on a centimetre-grade
/// <see cref="RtkFixed"/> solution rather than a metre-grade <see cref="Autonomous"/>
/// one — the difference between an acceptable and an unacceptable vertex.
/// </summary>
public enum NmeaFixQuality
{
    /// <summary>No valid fix (GGA quality 0).</summary>
    None = 0,

    /// <summary>Standalone GNSS fix, ~metre accuracy (GGA quality 1).</summary>
    Autonomous = 1,

    /// <summary>Differential GNSS, sub-metre (GGA quality 2).</summary>
    Differential = 2,

    /// <summary>Precise Positioning Service fix (GGA quality 3).</summary>
    PpsFix = 3,

    /// <summary>RTK fixed-integer solution, centimetre accuracy (GGA quality 4).</summary>
    RtkFixed = 4,

    /// <summary>RTK float solution, decimetre accuracy (GGA quality 5).</summary>
    RtkFloat = 5,

    /// <summary>Dead-reckoning / estimated (GGA quality 6).</summary>
    Estimated = 6,

    /// <summary>Manual input mode (GGA quality 7).</summary>
    Manual = 7,

    /// <summary>Simulation mode (GGA quality 8).</summary>
    Simulation = 8,
}
