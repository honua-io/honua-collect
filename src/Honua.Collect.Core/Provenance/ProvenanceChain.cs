namespace Honua.Collect.Core.Provenance;

/// <summary>
/// A hash-linked chain of custody for a single asset (BACKLOG, #41): an ordered,
/// append-only list of signed assertions tracing the asset from capture through
/// edits to sync. Each assertion references the prior link's hash, so the whole
/// chain is tamper-evident — verify it with <see cref="ProvenanceChainVerifier"/>.
/// </summary>
/// <param name="Assertions">The signed steps, in chain order (index 0 = capture).</param>
public sealed record ProvenanceChain(IReadOnlyList<SignedProvenanceAssertion> Assertions)
{
    /// <summary>The genesis (capture) assertion, or <see langword="null"/> for an empty chain.</summary>
    public SignedProvenanceAssertion? Genesis => Assertions.Count > 0 ? Assertions[0] : null;

    /// <summary>The most recent assertion, or <see langword="null"/> for an empty chain.</summary>
    public SignedProvenanceAssertion? Head => Assertions.Count > 0 ? Assertions[^1] : null;

    /// <summary>The content hash of the asset as of the head step — what current bytes must match.</summary>
    public string? HeadContentSha256 => Head?.Assertion.ContentSha256;
}
