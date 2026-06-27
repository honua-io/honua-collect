using Honua.Collect.Core.Licensing;

namespace Honua.Collect.Core.Provenance;

/// <summary>How a chain-of-custody verification failed (or that it passed).</summary>
public enum ProvenanceChainStatus
{
    /// <summary>Every assertion signed, every link intact, and (when checked) the head content matches.</summary>
    Valid,

    /// <summary>The chain held no assertions.</summary>
    Empty,

    /// <summary>An assertion or its signer key was structurally unusable.</summary>
    Malformed,

    /// <summary>An assertion's signature did not verify under its declared signer key — forged or altered step.</summary>
    SignatureInvalid,

    /// <summary>An assertion's sequence number was out of order (chain reordered or spliced).</summary>
    SequenceBroken,

    /// <summary>An assertion's prior-hash did not match the actual prior link — a step was inserted, removed, or altered.</summary>
    LinkBroken,

    /// <summary>The genesis step was not a capture, or a later step claimed to be genesis.</summary>
    GenesisInvalid,

    /// <summary>Signatures and links are intact, but the supplied bytes do not match the head content hash — media swapped.</summary>
    ContentMismatch,

    /// <summary>
    /// Every signature and link is internally consistent, but a step that had to be
    /// anchored to a trusted device/authority key was signed by a key outside the
    /// supplied allowlist — i.e. a chain that is self-consistent but not provably from a
    /// registered signer. A completely re-forged chain (attacker's own keypair over
    /// forged media) is rejected here rather than passing as <see cref="Valid"/>.
    /// </summary>
    UntrustedSigner,
}

/// <summary>
/// The result of verifying a chain of custody. On failure <see cref="BreakIndex"/>
/// points at the offending assertion so a reviewer can see exactly where the chain
/// broke.
/// </summary>
/// <param name="Status">The verification outcome.</param>
/// <param name="BreakIndex">The index of the first failing assertion, or <see langword="null"/> when valid.</param>
public sealed record ProvenanceChainVerification(ProvenanceChainStatus Status, int? BreakIndex = null)
{
    /// <summary>Whether the chain is fully verified.</summary>
    public bool IsValid => Status == ProvenanceChainStatus.Valid;
}

/// <summary>
/// Independently verifies a hash-linked chain of custody (BACKLOG, #41) — the check a
/// server, CLI, or reviewer runs to answer "is this asset's full capture→edit→sync
/// history intact and untampered?". Walks the chain checking, for each link: the
/// signature (against the step's own signer key), the sequence order, and the
/// hash-link to the prior step; optionally that the current bytes still match the
/// head content hash. The first failure is reported precisely with its index.
///
/// <para>
/// <b>Trust anchoring.</b> The overloads that take no trusted-key allowlist prove the
/// chain is <em>internally consistent only</em>: every step is signed by the key it
/// declares and the links are unbroken. They deliberately do NOT prove the chain came
/// from a registered device or authority, because each link carries its own signer key
/// — so an attacker who fabricates a complete chain over forged media with their own
/// keypair produces an internally-consistent chain that these overloads report as
/// <see cref="ProvenanceChainStatus.Valid"/>. For a legal/compliance verdict, callers
/// MUST either use the overloads that take <c>trustedSignerKeys</c> (which anchor the
/// genesis — and optionally every — signer to an allowlist of registered keys) or pin
/// the genesis signer key to a registered device themselves. This mirrors the
/// external-key contract that <see cref="ProvenanceVerifier"/> already enforces for a
/// single signed manifest.
/// </para>
/// </summary>
public static class ProvenanceChainVerifier
{
    /// <summary>
    /// Verifies the chain's signatures and hash-links only (no content bytes and no
    /// trust anchor). <b>This proves internal consistency only</b> — it does not prove
    /// the chain came from a registered signer, so a fully re-forged chain passes. Use
    /// the <c>trustedSignerKeys</c> overload (or separately pin the genesis signer) for
    /// any decision that relies on the captor's identity.
    /// </summary>
    /// <param name="chain">The chain to verify.</param>
    /// <returns>The verification result.</returns>
    public static ProvenanceChainVerification VerifyChain(ProvenanceChain? chain)
        => VerifyChainCore(chain, trustedSignerKeys: null, requireEveryStepTrusted: false);

    /// <summary>
    /// Verifies the chain's signatures and hash-links <em>and</em> anchors it to a set
    /// of trusted signer keys, so a self-consistent but unregistered (re-forged) chain
    /// is rejected as <see cref="ProvenanceChainStatus.UntrustedSigner"/> rather than
    /// reported valid. The genesis (capture) step's signer key must be in
    /// <paramref name="trustedSignerKeys"/>; when
    /// <paramref name="requireEveryStepTrusted"/> is set, every step's signer must be.
    /// </summary>
    /// <param name="chain">The chain to verify.</param>
    /// <param name="trustedSignerKeys">
    /// The allowlist of raw 32-byte Ed25519 public keys of registered devices/authorities
    /// the chain may be anchored to. Must be non-empty.
    /// </param>
    /// <param name="requireEveryStepTrusted">
    /// When <see langword="true"/>, every link's signer key must be in the allowlist;
    /// when <see langword="false"/> (default), only the genesis capture is anchored.
    /// </param>
    /// <returns>The verification result.</returns>
    public static ProvenanceChainVerification VerifyChain(
        ProvenanceChain? chain,
        IReadOnlyCollection<byte[]> trustedSignerKeys,
        bool requireEveryStepTrusted = false)
    {
        ArgumentNullException.ThrowIfNull(trustedSignerKeys);
        if (trustedSignerKeys.Count == 0)
        {
            throw new ArgumentException(
                "At least one trusted signer key is required to anchor the chain.",
                nameof(trustedSignerKeys));
        }

        return VerifyChainCore(chain, trustedSignerKeys, requireEveryStepTrusted);
    }

    private static ProvenanceChainVerification VerifyChainCore(
        ProvenanceChain? chain,
        IReadOnlyCollection<byte[]>? trustedSignerKeys,
        bool requireEveryStepTrusted)
    {
        if (chain is null || chain.Assertions.Count == 0)
        {
            return new ProvenanceChainVerification(ProvenanceChainStatus.Empty);
        }

        string? expectedPriorHash = null;

        for (var i = 0; i < chain.Assertions.Count; i++)
        {
            var link = chain.Assertions[i];
            if (link?.Assertion is null
                || string.IsNullOrEmpty(link.SignatureBase64)
                || string.IsNullOrEmpty(link.SignerPublicKeyBase64))
            {
                return new ProvenanceChainVerification(ProvenanceChainStatus.Malformed, i);
            }

            var assertion = link.Assertion;

            // Genesis discipline: index 0 must be the capture with no prior; later
            // steps must declare a prior and must not be capture.
            if (i == 0)
            {
                if (assertion.Action != ProvenanceAction.Capture || assertion.PriorAssertionSha256 is not null)
                {
                    return new ProvenanceChainVerification(ProvenanceChainStatus.GenesisInvalid, 0);
                }
            }
            else if (assertion.Action == ProvenanceAction.Capture || assertion.PriorAssertionSha256 is null)
            {
                return new ProvenanceChainVerification(ProvenanceChainStatus.GenesisInvalid, i);
            }

            if (assertion.Sequence != i)
            {
                return new ProvenanceChainVerification(ProvenanceChainStatus.SequenceBroken, i);
            }

            // Hash-link: this step's prior-hash must equal the actual prior link hash.
            if (!string.Equals(assertion.PriorAssertionSha256, expectedPriorHash, StringComparison.Ordinal))
            {
                return new ProvenanceChainVerification(ProvenanceChainStatus.LinkBroken, i);
            }

            byte[] signature;
            byte[] signerKey;
            try
            {
                signature = Convert.FromBase64String(link.SignatureBase64);
                signerKey = Convert.FromBase64String(link.SignerPublicKeyBase64);
            }
            catch (FormatException)
            {
                return new ProvenanceChainVerification(ProvenanceChainStatus.Malformed, i);
            }

            if (!Ed25519Signing.Verify(assertion.ToCanonicalBytes(), signature, signerKey))
            {
                return new ProvenanceChainVerification(ProvenanceChainStatus.SignatureInvalid, i);
            }

            // Trust anchor: a valid signature only proves the declared key signed this
            // step — the forger controls that key. Require the genesis (and optionally
            // every) signer to be a registered/trusted key, so a re-forged-from-scratch
            // chain cannot pass as Valid.
            if (trustedSignerKeys is not null
                && (i == 0 || requireEveryStepTrusted)
                && !IsTrusted(trustedSignerKeys, signerKey))
            {
                return new ProvenanceChainVerification(ProvenanceChainStatus.UntrustedSigner, i);
            }

            expectedPriorHash = assertion.LinkHash();
        }

        return new ProvenanceChainVerification(ProvenanceChainStatus.Valid);
    }

    private static bool IsTrusted(IReadOnlyCollection<byte[]> trustedSignerKeys, byte[] signerKey)
    {
        foreach (var trusted in trustedSignerKeys)
        {
            if (trusted is not null && trusted.AsSpan().SequenceEqual(signerKey))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fully verifies the chain <em>and</em> that the supplied current bytes still hash
    /// to the head assertion's content digest. An intact chain over swapped media is
    /// reported as <see cref="ProvenanceChainStatus.ContentMismatch"/> at the head index,
    /// distinct from a broken link or bad signature. <b>No trust anchor</b> — see
    /// <see cref="VerifyChain(ProvenanceChain?)"/>; use the <c>trustedSignerKeys</c>
    /// overload for an identity-bound verdict.
    /// </summary>
    /// <param name="chain">The chain to verify.</param>
    /// <param name="currentContent">The current asset bytes to check against the head hash.</param>
    /// <returns>The verification result.</returns>
    public static ProvenanceChainVerification Verify(ProvenanceChain? chain, byte[] currentContent)
        => VerifyCore(chain, currentContent, trustedSignerKeys: null, requireEveryStepTrusted: false);

    /// <summary>
    /// Fully verifies the chain, anchors it to <paramref name="trustedSignerKeys"/>, and
    /// checks the supplied current bytes against the head content digest. Combines the
    /// trust anchoring of
    /// <see cref="VerifyChain(ProvenanceChain?, IReadOnlyCollection{byte[]}, bool)"/>
    /// with the content check, so the result is a complete chain-of-custody verdict.
    /// </summary>
    /// <param name="chain">The chain to verify.</param>
    /// <param name="currentContent">The current asset bytes to check against the head hash.</param>
    /// <param name="trustedSignerKeys">Allowlist of trusted signer public keys; must be non-empty.</param>
    /// <param name="requireEveryStepTrusted">Whether every step (not just genesis) must be trusted.</param>
    /// <returns>The verification result.</returns>
    public static ProvenanceChainVerification Verify(
        ProvenanceChain? chain,
        byte[] currentContent,
        IReadOnlyCollection<byte[]> trustedSignerKeys,
        bool requireEveryStepTrusted = false)
    {
        ArgumentNullException.ThrowIfNull(trustedSignerKeys);
        if (trustedSignerKeys.Count == 0)
        {
            throw new ArgumentException(
                "At least one trusted signer key is required to anchor the chain.",
                nameof(trustedSignerKeys));
        }

        return VerifyCore(chain, currentContent, trustedSignerKeys, requireEveryStepTrusted);
    }

    private static ProvenanceChainVerification VerifyCore(
        ProvenanceChain? chain,
        byte[] currentContent,
        IReadOnlyCollection<byte[]>? trustedSignerKeys,
        bool requireEveryStepTrusted)
    {
        ArgumentNullException.ThrowIfNull(currentContent);

        var chainResult = VerifyChainCore(chain, trustedSignerKeys, requireEveryStepTrusted);
        if (!chainResult.IsValid)
        {
            return chainResult;
        }

        var headIndex = chain!.Assertions.Count - 1;
        return ContentHash.Matches(currentContent, chain.HeadContentSha256)
            ? new ProvenanceChainVerification(ProvenanceChainStatus.Valid)
            : new ProvenanceChainVerification(ProvenanceChainStatus.ContentMismatch, headIndex);
    }
}
