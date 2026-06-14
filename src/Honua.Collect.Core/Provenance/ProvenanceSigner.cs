using Honua.Collect.Core.Licensing;

namespace Honua.Collect.Core.Provenance;

/// <summary>
/// Signs capture provenance with an Ed25519 device key (BACKLOG, #41). On a real
/// device the private key is hardware-backed (Keystore / Secure Enclave) and never
/// leaves it; this is the platform-neutral signing operation over the manifest's
/// canonical bytes. Pairs with <see cref="ProvenanceVerifier"/>.
/// </summary>
public static class ProvenanceSigner
{
    /// <summary>
    /// Builds and signs a provenance manifest binding the content hash, capture
    /// time, user, device, and (optional) position.
    /// </summary>
    /// <param name="contentSha256">Lowercase-hex SHA-256 of the captured content (see <see cref="ContentHash"/>).</param>
    /// <param name="capturedAtUtc">Capture time (UTC).</param>
    /// <param name="capturingUserId">The capturing user.</param>
    /// <param name="deviceId">The capturing device id.</param>
    /// <param name="privateKey">The raw 32-byte Ed25519 device private key.</param>
    /// <param name="latitude">Latitude at capture, if known.</param>
    /// <param name="longitude">Longitude at capture, if known.</param>
    /// <param name="accuracyMeters">Horizontal accuracy of the position, if known.</param>
    /// <returns>The signed provenance.</returns>
    public static SignedProvenance Sign(
        string contentSha256,
        DateTimeOffset capturedAtUtc,
        string capturingUserId,
        string deviceId,
        byte[] privateKey,
        double? latitude = null,
        double? longitude = null,
        double? accuracyMeters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(capturingUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(privateKey);

        var manifest = new CaptureProvenance
        {
            ContentSha256 = contentSha256,
            CapturedAtUtc = capturedAtUtc,
            CapturingUserId = capturingUserId,
            DeviceId = deviceId,
            Latitude = latitude,
            Longitude = longitude,
            AccuracyMeters = accuracyMeters,
        };

        return Sign(manifest, privateKey);
    }

    /// <summary>Signs an existing manifest with a raw Ed25519 private key.</summary>
    /// <param name="manifest">The provenance manifest.</param>
    /// <param name="privateKey">The raw 32-byte Ed25519 device private key.</param>
    /// <returns>The signed provenance.</returns>
    public static SignedProvenance Sign(CaptureProvenance manifest, byte[] privateKey)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(privateKey);

        var signature = Ed25519Signing.Sign(manifest.ToCanonicalBytes(), privateKey);
        return new SignedProvenance(manifest, Convert.ToBase64String(signature));
    }
}
