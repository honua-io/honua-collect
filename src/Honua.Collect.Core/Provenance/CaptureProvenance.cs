using System.Globalization;
using System.Text;

namespace Honua.Collect.Core.Provenance;

/// <summary>
/// A verifiable capture provenance manifest (BACKLOG, #41): who captured what,
/// when, and where, bound to the content's hash. Signed (see
/// <see cref="ProvenanceSigner"/>) it answers the question incumbents can't —
/// "can you prove this photo wasn't faked, edited, or relocated?" — because any
/// change to the media (hash) or the metadata invalidates the signature.
/// </summary>
public sealed record CaptureProvenance
{
    /// <summary>Manifest format version, for forward compatibility.</summary>
    public const string Version = "HPRV1";

    /// <summary>Lowercase-hex SHA-256 of the captured content (e.g. the photo bytes).</summary>
    public required string ContentSha256 { get; init; }

    /// <summary>When the content was captured (UTC).</summary>
    public required DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>The user who captured it.</summary>
    public required string CapturingUserId { get; init; }

    /// <summary>The device it was captured on (stable per-install / hardware id).</summary>
    public required string DeviceId { get; init; }

    /// <summary>Latitude in decimal degrees at capture, when a position was available.</summary>
    public double? Latitude { get; init; }

    /// <summary>Longitude in decimal degrees at capture, when a position was available.</summary>
    public double? Longitude { get; init; }

    /// <summary>Horizontal accuracy in metres for the bound position, when known.</summary>
    public double? AccuracyMeters { get; init; }

    /// <summary>
    /// The canonical, deterministic byte encoding that is signed and verified. A fixed
    /// field order with invariant formatting makes the signature reproducible across
    /// platforms; null coordinates render as empty so present-vs-absent is unambiguous.
    /// </summary>
    /// <returns>The bytes to sign / verify.</returns>
    public byte[] ToCanonicalBytes()
    {
        var builder = new StringBuilder();
        builder.Append(Version).Append('\n');
        builder.Append("content=").Append(ContentSha256).Append('\n');
        builder.Append("capturedAt=").Append(CapturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("user=").Append(CapturingUserId).Append('\n');
        builder.Append("device=").Append(DeviceId).Append('\n');
        builder.Append("lat=").Append(Format(Latitude)).Append('\n');
        builder.Append("lon=").Append(Format(Longitude)).Append('\n');
        builder.Append("acc=").Append(Format(AccuracyMeters));
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static string Format(double? value)
        => value?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty;
}
