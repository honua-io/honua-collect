using Honua.Collect.Core.Enterprise;
using Honua.Collect.Core.Licensing;

namespace Honua.Collect.Core.Provenance;

/// <summary>
/// Builds a hash-linked chain of custody (BACKLOG, #41) one signed step at a time:
/// <see cref="StartCapture"/> creates the genesis assertion, then <see cref="AppendEdit"/>
/// and <see cref="AppendSync"/> append further links that reference the prior step's
/// hash. Each link is signed with the step's Ed25519 key (on a real device that key is
/// hardware-backed; here it is the platform-neutral signing operation). Pairs with
/// <see cref="ProvenanceChainVerifier"/>.
/// </summary>
public static class ProvenanceChainBuilder
{
    /// <summary>
    /// Starts a chain with the genesis (capture) assertion binding the captured
    /// content hash, capturing actor, device, time, and (optional) position.
    /// </summary>
    /// <param name="contentSha256">Lowercase-hex SHA-256 of the captured content (see <see cref="ContentHash"/>).</param>
    /// <param name="capturedAtUtc">Capture time (UTC).</param>
    /// <param name="actorId">The capturing user.</param>
    /// <param name="deviceId">The capturing device id.</param>
    /// <param name="privateKey">The raw 32-byte Ed25519 device private key.</param>
    /// <param name="latitude">Latitude at capture, if known.</param>
    /// <param name="longitude">Longitude at capture, if known.</param>
    /// <param name="accuracyMeters">Horizontal accuracy of the position, if known.</param>
    /// <returns>A one-link chain.</returns>
    public static ProvenanceChain StartCapture(
        string contentSha256,
        DateTimeOffset capturedAtUtc,
        string actorId,
        string deviceId,
        byte[] privateKey,
        double? latitude = null,
        double? longitude = null,
        double? accuracyMeters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(privateKey);

        var capture = new ProvenanceAssertion
        {
            Sequence = 0,
            Action = ProvenanceAction.Capture,
            ContentSha256 = contentSha256,
            PriorAssertionSha256 = null,
            TimestampUtc = capturedAtUtc,
            ActorId = actorId,
            DeviceId = deviceId,
            Latitude = latitude,
            Longitude = longitude,
            AccuracyMeters = accuracyMeters,
        };

        return new ProvenanceChain([Sign(capture, privateKey)]);
    }

    /// <summary>
    /// Appends an <see cref="ProvenanceAction.Edit"/> step recording new content
    /// (e.g. after a provenance-preserving redaction) hash-linked to the prior head.
    /// </summary>
    /// <param name="chain">The chain to extend (not mutated; a new chain is returned).</param>
    /// <param name="newContentSha256">Lowercase-hex SHA-256 of the content after the edit.</param>
    /// <param name="editedAtUtc">When the edit happened (UTC).</param>
    /// <param name="actorId">Who made the edit.</param>
    /// <param name="deviceId">The device the edit was made on.</param>
    /// <param name="privateKey">The raw 32-byte Ed25519 signing key for this step.</param>
    /// <param name="note">Optional human note (e.g. "blurred faces").</param>
    /// <returns>The extended chain.</returns>
    public static ProvenanceChain AppendEdit(
        ProvenanceChain chain,
        string newContentSha256,
        DateTimeOffset editedAtUtc,
        string actorId,
        string deviceId,
        byte[] privateKey,
        string? note = null)
        => Append(chain, ProvenanceAction.Edit, newContentSha256, editedAtUtc, actorId, deviceId, privateKey, note);

    /// <summary>
    /// Appends a <see cref="ProvenanceAction.Sync"/> step (the asset reached the server)
    /// hash-linked to the prior head. Content is carried forward unchanged.
    /// </summary>
    /// <param name="chain">The chain to extend.</param>
    /// <param name="syncedAtUtc">When the sync happened (UTC).</param>
    /// <param name="actorId">The sync principal (often the same operator, or a service identity).</param>
    /// <param name="deviceId">The device that synced.</param>
    /// <param name="privateKey">The raw 32-byte Ed25519 signing key for this step.</param>
    /// <param name="note">Optional human note.</param>
    /// <returns>The extended chain.</returns>
    public static ProvenanceChain AppendSync(
        ProvenanceChain chain,
        DateTimeOffset syncedAtUtc,
        string actorId,
        string deviceId,
        byte[] privateKey,
        string? note = null)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var head = chain.Head ?? throw new InvalidOperationException("Cannot sync an empty chain; start with a capture.");
        return Append(chain, ProvenanceAction.Sync, head.Assertion.ContentSha256, syncedAtUtc, actorId, deviceId, privateKey, note);
    }

    /// <summary>
    /// Convenience overload that draws the operator identity from the current auth
    /// session (BACKLOG E1) so the chain step is attributed to the signed-in user.
    /// </summary>
    /// <param name="chain">The chain to extend.</param>
    /// <param name="newContentSha256">Lowercase-hex SHA-256 of the content after the edit.</param>
    /// <param name="editedAtUtc">When the edit happened (UTC).</param>
    /// <param name="session">The authenticated session whose <see cref="AuthSession.UserId"/> is the actor.</param>
    /// <param name="deviceId">The device the edit was made on.</param>
    /// <param name="privateKey">The raw 32-byte Ed25519 signing key for this step.</param>
    /// <param name="note">Optional human note.</param>
    /// <returns>The extended chain.</returns>
    public static ProvenanceChain AppendEdit(
        ProvenanceChain chain,
        string newContentSha256,
        DateTimeOffset editedAtUtc,
        AuthSession session,
        string deviceId,
        byte[] privateKey,
        string? note = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return AppendEdit(chain, newContentSha256, editedAtUtc, session.UserId, deviceId, privateKey, note);
    }

    private static ProvenanceChain Append(
        ProvenanceChain chain,
        ProvenanceAction action,
        string contentSha256,
        DateTimeOffset atUtc,
        string actorId,
        string deviceId,
        byte[] privateKey,
        string? note)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(privateKey);

        var head = chain.Head ?? throw new InvalidOperationException("Cannot append to an empty chain; start with a capture.");

        var next = new ProvenanceAssertion
        {
            Sequence = head.Assertion.Sequence + 1,
            Action = action,
            ContentSha256 = contentSha256,
            PriorAssertionSha256 = head.Assertion.LinkHash(),
            TimestampUtc = atUtc,
            ActorId = actorId,
            DeviceId = deviceId,
            Note = note,
        };

        var appended = new List<SignedProvenanceAssertion>(chain.Assertions) { Sign(next, privateKey) };
        return new ProvenanceChain(appended);
    }

    private static SignedProvenanceAssertion Sign(ProvenanceAssertion assertion, byte[] privateKey)
    {
        var signature = Ed25519Signing.Sign(assertion.ToCanonicalBytes(), privateKey);
        var publicKey = Ed25519Signing.PublicKeyFromPrivate(privateKey);
        return new SignedProvenanceAssertion(
            assertion,
            Convert.ToBase64String(signature),
            Convert.ToBase64String(publicKey));
    }
}
