using System.Text;
using Honua.Collect.Core.Enterprise;
using Honua.Collect.Core.Licensing;
using Honua.Collect.Core.Provenance;

namespace Honua.Collect.Core.Tests.Provenance;

public class ProvenanceChainTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 6, 14, 9, 30, 0, TimeSpan.Zero);
    private static readonly byte[] Photo = Encoding.UTF8.GetBytes("...pretend JPEG bytes...");
    private static readonly byte[] Redacted = Encoding.UTF8.GetBytes("...redacted JPEG bytes...");

    private static ProvenanceChain CaptureOnly(byte[] priv, byte[]? content = null) =>
        ProvenanceChainBuilder.StartCapture(
            ContentHash.Sha256Hex(content ?? Photo),
            CapturedAt,
            actorId: "field-user-7",
            deviceId: "device-abc",
            privateKey: priv,
            latitude: 48.1173,
            longitude: 11.51667,
            accuracyMeters: 0.03);

    // --- happy path -----------------------------------------------------------

    [Fact]
    public void Single_capture_chain_verifies()
    {
        var (priv, _) = Ed25519Signing.GenerateKeyPair();
        var chain = CaptureOnly(priv);

        Assert.True(ProvenanceChainVerifier.VerifyChain(chain).IsValid);
        Assert.Equal(ProvenanceChainStatus.Valid, ProvenanceChainVerifier.Verify(chain, Photo).Status);
        Assert.Equal(ProvenanceAction.Capture, chain.Genesis!.Assertion.Action);
        Assert.Null(chain.Genesis.Assertion.PriorAssertionSha256);
    }

    [Fact]
    public void Multi_step_capture_edit_sync_chain_is_hash_linked_and_verifies()
    {
        var (capPriv, _) = Ed25519Signing.GenerateKeyPair();
        var (editPriv, _) = Ed25519Signing.GenerateKeyPair();
        var (syncPriv, _) = Ed25519Signing.GenerateKeyPair();

        var chain = CaptureOnly(capPriv);
        chain = ProvenanceChainBuilder.AppendEdit(
            chain, ContentHash.Sha256Hex(Redacted), CapturedAt.AddMinutes(5),
            actorId: "field-user-7", deviceId: "device-abc", privateKey: editPriv, note: "blurred faces");
        chain = ProvenanceChainBuilder.AppendSync(
            chain, CapturedAt.AddMinutes(20),
            actorId: "sync-service", deviceId: "device-abc", privateKey: syncPriv);

        Assert.Equal(3, chain.Assertions.Count);

        // Each step links to the actual prior link hash.
        Assert.Equal(chain.Assertions[0].Assertion.LinkHash(), chain.Assertions[1].Assertion.PriorAssertionSha256);
        Assert.Equal(chain.Assertions[1].Assertion.LinkHash(), chain.Assertions[2].Assertion.PriorAssertionSha256);

        // Head content is the redacted bytes (carried through the sync step).
        Assert.True(ProvenanceChainVerifier.VerifyChain(chain).IsValid);
        Assert.Equal(ProvenanceChainStatus.Valid, ProvenanceChainVerifier.Verify(chain, Redacted).Status);
        Assert.Equal(ProvenanceAction.Sync, chain.Head!.Assertion.Action);
    }

    [Fact]
    public void Append_overload_uses_auth_session_user_as_actor()
    {
        var (capPriv, _) = Ed25519Signing.GenerateKeyPair();
        var (editPriv, _) = Ed25519Signing.GenerateKeyPair();
        var session = new AuthSession
        {
            UserId = "sso-user-42",
            AccessToken = "token",
            ExpiresAtUtc = CapturedAt.AddHours(1),
        };

        var chain = ProvenanceChainBuilder.AppendEdit(
            CaptureOnly(capPriv), ContentHash.Sha256Hex(Redacted), CapturedAt.AddMinutes(1),
            session, deviceId: "device-abc", privateKey: editPriv);

        Assert.Equal("sso-user-42", chain.Head!.Assertion.ActorId);
        Assert.True(ProvenanceChainVerifier.VerifyChain(chain).IsValid);
    }

    // --- tamper detection -----------------------------------------------------

    [Fact]
    public void Swapped_head_media_is_content_mismatch_even_with_an_intact_chain()
    {
        var (priv, _) = Ed25519Signing.GenerateKeyPair();
        var chain = CaptureOnly(priv);

        var result = ProvenanceChainVerifier.Verify(chain, Encoding.UTF8.GetBytes("...edited bytes..."));

        Assert.Equal(ProvenanceChainStatus.ContentMismatch, result.Status);
        Assert.Equal(0, result.BreakIndex);
        Assert.True(ProvenanceChainVerifier.VerifyChain(chain).IsValid); // chain itself is fine
    }

    [Fact]
    public void Tampering_with_a_middle_assertion_breaks_its_signature()
    {
        var (capPriv, _) = Ed25519Signing.GenerateKeyPair();
        var (editPriv, _) = Ed25519Signing.GenerateKeyPair();

        var chain = CaptureOnly(capPriv);
        chain = ProvenanceChainBuilder.AppendEdit(
            chain, ContentHash.Sha256Hex(Redacted), CapturedAt.AddMinutes(5),
            "field-user-7", "device-abc", editPriv);

        // Move the recorded location of the edit step after signing.
        var tampered = chain.Assertions[1] with
        {
            Assertion = chain.Assertions[1].Assertion with { Latitude = 51.5, Longitude = -0.12 },
        };
        var broken = new ProvenanceChain([chain.Assertions[0], tampered]);

        var result = ProvenanceChainVerifier.VerifyChain(broken);
        Assert.Equal(ProvenanceChainStatus.SignatureInvalid, result.Status);
        Assert.Equal(1, result.BreakIndex);
    }

    [Fact]
    public void Removing_a_link_breaks_the_hash_chain()
    {
        var (capPriv, _) = Ed25519Signing.GenerateKeyPair();
        var (editPriv, _) = Ed25519Signing.GenerateKeyPair();
        var (syncPriv, _) = Ed25519Signing.GenerateKeyPair();

        var chain = CaptureOnly(capPriv);
        chain = ProvenanceChainBuilder.AppendEdit(chain, ContentHash.Sha256Hex(Redacted), CapturedAt.AddMinutes(5), "u", "d", editPriv);
        chain = ProvenanceChainBuilder.AppendSync(chain, CapturedAt.AddMinutes(20), "u", "d", syncPriv);

        // Drop the middle (edit) link. The sync step's prior-hash no longer matches.
        var spliced = new ProvenanceChain([chain.Assertions[0], chain.Assertions[2]]);

        var result = ProvenanceChainVerifier.VerifyChain(spliced);
        Assert.Equal(ProvenanceChainStatus.SequenceBroken, result.Status);
        Assert.Equal(1, result.BreakIndex);
    }

    [Fact]
    public void Reparenting_a_link_to_a_forged_prior_hash_is_detected_as_link_broken()
    {
        var (capPriv, _) = Ed25519Signing.GenerateKeyPair();
        var (editPriv, _) = Ed25519Signing.GenerateKeyPair();

        var chain = CaptureOnly(capPriv);
        chain = ProvenanceChainBuilder.AppendEdit(chain, ContentHash.Sha256Hex(Redacted), CapturedAt.AddMinutes(5), "u", "d", editPriv);

        // Re-sign the edit step with a different prior hash (a re-rooted chain). The
        // signature is valid for the forged assertion, but the link no longer matches
        // the genesis hash.
        var forgedAssertion = chain.Assertions[1].Assertion with
        {
            PriorAssertionSha256 = new string('0', 64),
        };
        var resigned = new SignedProvenanceAssertion(
            forgedAssertion,
            Convert.ToBase64String(Ed25519Signing.Sign(forgedAssertion.ToCanonicalBytes(), editPriv)),
            chain.Assertions[1].SignerPublicKeyBase64);
        var forged = new ProvenanceChain([chain.Assertions[0], resigned]);

        var result = ProvenanceChainVerifier.VerifyChain(forged);
        Assert.Equal(ProvenanceChainStatus.LinkBroken, result.Status);
        Assert.Equal(1, result.BreakIndex);
    }

    [Fact]
    public void A_step_signed_by_the_wrong_key_for_its_declared_signer_is_rejected()
    {
        var (capPriv, _) = Ed25519Signing.GenerateKeyPair();
        var (_, otherPub) = Ed25519Signing.GenerateKeyPair();

        var chain = CaptureOnly(capPriv);

        // Keep the capture's signature but claim a different signer public key.
        var mismatched = chain.Assertions[0] with
        {
            SignerPublicKeyBase64 = Convert.ToBase64String(otherPub),
        };

        var result = ProvenanceChainVerifier.VerifyChain(new ProvenanceChain([mismatched]));
        Assert.Equal(ProvenanceChainStatus.SignatureInvalid, result.Status);
        Assert.Equal(0, result.BreakIndex);
    }

    [Fact]
    public void A_chain_that_does_not_start_with_capture_is_genesis_invalid()
    {
        var (capPriv, _) = Ed25519Signing.GenerateKeyPair();
        var (editPriv, _) = Ed25519Signing.GenerateKeyPair();

        var chain = CaptureOnly(capPriv);
        chain = ProvenanceChainBuilder.AppendEdit(chain, ContentHash.Sha256Hex(Redacted), CapturedAt.AddMinutes(5), "u", "d", editPriv);

        // Take only the (non-genesis) edit step as a standalone chain.
        var headless = new ProvenanceChain([chain.Assertions[1]]);

        var result = ProvenanceChainVerifier.VerifyChain(headless);
        Assert.Equal(ProvenanceChainStatus.GenesisInvalid, result.Status);
        Assert.Equal(0, result.BreakIndex);
    }

    [Fact]
    public void Malformed_signature_in_a_step_is_reported_with_its_index()
    {
        var (priv, _) = Ed25519Signing.GenerateKeyPair();
        var chain = CaptureOnly(priv);
        var malformed = new ProvenanceChain([chain.Assertions[0] with { SignatureBase64 = "not-base64-!!" }]);

        var result = ProvenanceChainVerifier.VerifyChain(malformed);
        Assert.Equal(ProvenanceChainStatus.Malformed, result.Status);
        Assert.Equal(0, result.BreakIndex);
    }

    // --- trust anchoring ------------------------------------------------------

    [Fact]
    public void A_fully_reforged_chain_passes_internal_checks_but_is_rejected_when_anchored()
    {
        // An attacker fabricates an internally-consistent chain over forged media,
        // signing every step with their OWN keypair.
        var (attackerPriv, _) = Ed25519Signing.GenerateKeyPair();
        var (_, registeredDevicePub) = Ed25519Signing.GenerateKeyPair();
        var forged = CaptureOnly(attackerPriv);

        // Internal-consistency-only check is fooled (the documented limitation)...
        Assert.True(ProvenanceChainVerifier.VerifyChain(forged).IsValid);

        // ...but anchoring the genesis to a registered device key rejects it.
        var anchored = ProvenanceChainVerifier.VerifyChain(forged, new[] { registeredDevicePub });
        Assert.Equal(ProvenanceChainStatus.UntrustedSigner, anchored.Status);
        Assert.Equal(0, anchored.BreakIndex);

        var anchoredWithContent = ProvenanceChainVerifier.Verify(forged, Photo, new[] { registeredDevicePub });
        Assert.Equal(ProvenanceChainStatus.UntrustedSigner, anchoredWithContent.Status);
    }

    [Fact]
    public void A_genuine_chain_signed_by_the_trusted_genesis_key_verifies_when_anchored()
    {
        var (priv, pub) = Ed25519Signing.GenerateKeyPair();
        var chain = CaptureOnly(priv);

        Assert.True(ProvenanceChainVerifier.VerifyChain(chain, new[] { pub }).IsValid);
        Assert.Equal(ProvenanceChainStatus.Valid, ProvenanceChainVerifier.Verify(chain, Photo, new[] { pub }).Status);
    }

    [Fact]
    public void Require_every_step_trusted_rejects_a_step_signed_by_an_unregistered_key()
    {
        var (capPriv, capPub) = Ed25519Signing.GenerateKeyPair();
        var (editPriv, editPub) = Ed25519Signing.GenerateKeyPair();

        var chain = CaptureOnly(capPriv);
        chain = ProvenanceChainBuilder.AppendEdit(
            chain, ContentHash.Sha256Hex(Redacted), CapturedAt.AddMinutes(5),
            actorId: "field-user-7", deviceId: "device-abc", privateKey: editPriv, note: "blurred faces");

        // Anchoring only the genesis is satisfied — the capture signer is trusted.
        Assert.True(ProvenanceChainVerifier.VerifyChain(chain, new[] { capPub }).IsValid);

        // Requiring every step exposes the edit step's unregistered signer.
        var strict = ProvenanceChainVerifier.VerifyChain(chain, new[] { capPub }, requireEveryStepTrusted: true);
        Assert.Equal(ProvenanceChainStatus.UntrustedSigner, strict.Status);
        Assert.Equal(1, strict.BreakIndex);

        // With both signers registered, the strict check passes.
        Assert.True(ProvenanceChainVerifier.VerifyChain(
            chain, new[] { capPub, editPub }, requireEveryStepTrusted: true).IsValid);
    }

    [Fact]
    public void Anchored_overloads_require_a_non_empty_trusted_key_set()
    {
        var (priv, _) = Ed25519Signing.GenerateKeyPair();
        var chain = CaptureOnly(priv);

        Assert.Throws<ArgumentException>(() =>
            ProvenanceChainVerifier.VerifyChain(chain, Array.Empty<byte[]>()));
        Assert.Throws<ArgumentNullException>(() =>
            ProvenanceChainVerifier.VerifyChain(chain, null!));
        Assert.Throws<ArgumentException>(() =>
            ProvenanceChainVerifier.Verify(chain, Photo, Array.Empty<byte[]>()));
    }

    [Fact]
    public void Empty_chain_is_reported()
    {
        Assert.Equal(ProvenanceChainStatus.Empty, ProvenanceChainVerifier.VerifyChain(new ProvenanceChain([])).Status);
        Assert.Equal(ProvenanceChainStatus.Empty, ProvenanceChainVerifier.VerifyChain(null).Status);
    }

    [Fact]
    public void Builder_guards_empty_and_invalid_inputs()
    {
        var (priv, _) = Ed25519Signing.GenerateKeyPair();
        Assert.Throws<InvalidOperationException>(() =>
            ProvenanceChainBuilder.AppendSync(new ProvenanceChain([]), CapturedAt, "u", "d", priv));
        Assert.Throws<ArgumentException>(() =>
            ProvenanceChainBuilder.StartCapture("hash", CapturedAt, "", "d", priv));
        Assert.Throws<ArgumentNullException>(() =>
            ProvenanceChainVerifier.Verify(CaptureOnly(priv), null!));
    }
}
