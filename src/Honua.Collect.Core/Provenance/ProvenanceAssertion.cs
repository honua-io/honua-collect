using System.Globalization;
using System.Text;

namespace Honua.Collect.Core.Provenance;

/// <summary>
/// The kind of step an assertion records in an asset's chain of custody. Mirrors
/// the real-world lifecycle of a captured record: first captured, then edited
/// (e.g. redaction, field correction), then synced to the server.
/// </summary>
public enum ProvenanceAction
{
    /// <summary>The originating capture — the first link in the chain.</summary>
    Capture,

    /// <summary>An edit to the asset (field change, redaction, re-encode).</summary>
    Edit,

    /// <summary>The asset (or its record) was synced to the server.</summary>
    Sync,
}

/// <summary>
/// One signed link in a hash-linked chain of custody (BACKLOG, #41). Each assertion
/// binds an action (capture / edit / sync) to the content hash <em>as of that step</em>,
/// the acting identity, a UTC timestamp, and — crucially — the hash of the
/// <em>previous</em> assertion, so the steps form a tamper-evident chain: altering or
/// removing any earlier link changes every later <see cref="ToCanonicalBytes"/> and
/// breaks its signature.
/// </summary>
/// <remarks>
/// This is the C2PA-aligned shape (an <c>assertion</c> over a <c>claim</c>, signed)
/// expressed platform-neutrally. A future C2PA manifest (<c>.c2pa</c>) would carry the
/// same fields as standard assertions (<c>c2pa.actions</c>, <c>stds.exif</c>, a content
/// hard-binding) inside the embedded manifest store; <see cref="PriorAssertionSha256"/>
/// plays the role of the C2PA <c>parent</c>/ingredient hash-binding.
/// </remarks>
public sealed record ProvenanceAssertion
{
    /// <summary>Assertion format version, for forward compatibility.</summary>
    public const string Version = "HPRA1";

    /// <summary>Zero-based position of this assertion in the chain (0 = capture).</summary>
    public required int Sequence { get; init; }

    /// <summary>What this step did to the asset.</summary>
    public required ProvenanceAction Action { get; init; }

    /// <summary>Lowercase-hex SHA-256 of the asset content as of this step.</summary>
    public required string ContentSha256 { get; init; }

    /// <summary>
    /// Lowercase-hex SHA-256 of the <em>prior</em> assertion's canonical bytes, or
    /// <see langword="null"/> for the genesis (capture) assertion. This is the
    /// hash-link that makes the chain tamper-evident.
    /// </summary>
    public string? PriorAssertionSha256 { get; init; }

    /// <summary>When this step occurred (UTC).</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>The acting identity (capturing user, editor, or sync principal).</summary>
    public required string ActorId { get; init; }

    /// <summary>The device the step occurred on (stable per-install / hardware id).</summary>
    public required string DeviceId { get; init; }

    /// <summary>Latitude in decimal degrees, when a position was available.</summary>
    public double? Latitude { get; init; }

    /// <summary>Longitude in decimal degrees, when a position was available.</summary>
    public double? Longitude { get; init; }

    /// <summary>Horizontal accuracy in metres for the bound position, when known.</summary>
    public double? AccuracyMeters { get; init; }

    /// <summary>Optional human note (e.g. "blurred faces", "corrected serial").</summary>
    public string? Note { get; init; }

    /// <summary>
    /// The canonical, deterministic byte encoding that is signed, verified, and
    /// hashed to form the next assertion's <see cref="PriorAssertionSha256"/>. A fixed
    /// field order with invariant formatting makes the chain reproducible across
    /// platforms; null fields render as empty so present-vs-absent is unambiguous.
    /// </summary>
    /// <returns>The bytes to sign / verify / hash-link.</returns>
    public byte[] ToCanonicalBytes()
    {
        var builder = new StringBuilder();
        builder.Append(Version).Append('\n');
        builder.Append("seq=").Append(Sequence.ToString(CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("action=").Append(Action.ToString()).Append('\n');
        builder.Append("content=").Append(ContentSha256).Append('\n');
        builder.Append("prior=").Append(PriorAssertionSha256 ?? string.Empty).Append('\n');
        builder.Append("at=").Append(TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("actor=").Append(ActorId).Append('\n');
        builder.Append("device=").Append(DeviceId).Append('\n');
        builder.Append("lat=").Append(Format(Latitude)).Append('\n');
        builder.Append("lon=").Append(Format(Longitude)).Append('\n');
        builder.Append("acc=").Append(Format(AccuracyMeters)).Append('\n');
        builder.Append("note=").Append(Note ?? string.Empty);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>The lowercase-hex SHA-256 of this assertion's canonical bytes (its link hash).</summary>
    /// <returns>The 64-character hex digest later steps reference.</returns>
    public string LinkHash() => ContentHash.Sha256Hex(ToCanonicalBytes());

    private static string Format(double? value)
        => value?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>A <see cref="ProvenanceAssertion"/> together with its Ed25519 signature.</summary>
/// <param name="Assertion">The assertion (one chain link).</param>
/// <param name="SignatureBase64">Base64 Ed25519 signature over <see cref="ProvenanceAssertion.ToCanonicalBytes"/>.</param>
/// <param name="SignerPublicKeyBase64">
/// Base64 raw 32-byte Ed25519 public key that signed this step. Carrying the per-step
/// signer key lets a chain mix actors (a field device, then a server sync key) while a
/// verifier still checks each link against its own signer. Trust in those keys (a real
/// cert/CA chain) is the deferred step — see the epic.
/// </param>
public sealed record SignedProvenanceAssertion(
    ProvenanceAssertion Assertion,
    string SignatureBase64,
    string SignerPublicKeyBase64);
