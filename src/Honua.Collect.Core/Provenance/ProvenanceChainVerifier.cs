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
/// </summary>
public static class ProvenanceChainVerifier
{
    /// <summary>
    /// Verifies the chain's signatures and hash-links only (no content bytes).
    /// </summary>
    /// <param name="chain">The chain to verify.</param>
    /// <returns>The verification result.</returns>
    public static ProvenanceChainVerification VerifyChain(ProvenanceChain? chain)
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

            expectedPriorHash = assertion.LinkHash();
        }

        return new ProvenanceChainVerification(ProvenanceChainStatus.Valid);
    }

    /// <summary>
    /// Fully verifies the chain <em>and</em> that the supplied current bytes still hash
    /// to the head assertion's content digest. An intact chain over swapped media is
    /// reported as <see cref="ProvenanceChainStatus.ContentMismatch"/> at the head index,
    /// distinct from a broken link or bad signature.
    /// </summary>
    /// <param name="chain">The chain to verify.</param>
    /// <param name="currentContent">The current asset bytes to check against the head hash.</param>
    /// <returns>The verification result.</returns>
    public static ProvenanceChainVerification Verify(ProvenanceChain? chain, byte[] currentContent)
    {
        ArgumentNullException.ThrowIfNull(currentContent);

        var chainResult = VerifyChain(chain);
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
