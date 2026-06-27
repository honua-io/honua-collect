# Chain of custody & verifiable provenance

For legal, regulatory, insurance, and compliance submissions, the question is
*"can you prove this photo wasn't faked, edited, or relocated?"* Honua Collect can
answer it cryptographically. This documents what the shipped provenance code
proves, how to verify it, and its current limits.

## What is signed

At capture, Collect can bind a **capture provenance manifest**
(`Honua.Collect.Core.Provenance.CaptureProvenance`) to the captured media:

- a **SHA-256 hash of the content** (`ContentHash`) — the photo/file bytes;
- the **capture time** (UTC);
- the **capturing user**;
- the **device** identifier;
- the **GPS position** and its horizontal accuracy, when available.

The manifest has a deterministic canonical encoding and is signed with an
**Ed25519** device key (`ProvenanceSigner`). The signing and verification logic
is platform-neutral and unit-tested today; binding the private key to platform
hardware (Keystore / Secure Enclave) so it never leaves the device is a planned
device-side step that is **not yet wired** (see [Current limits](#current-limits-tracked-in-41)).

## What verification proves

An independent verifier — server, CLI, or reviewer — runs
`ProvenanceVerifier.Verify(signed, contentBytes, publicKey)` and gets one of:

| Result | Meaning |
| --- | --- |
| `Valid` | Signature checks out **and** the supplied media still hashes to the manifest — provenance intact. |
| `SignatureInvalid` | The manifest was altered (e.g. the location or timestamp was changed) — untrusted. |
| `ContentMismatch` | The signature is valid but the media bytes have changed — the photo was edited or swapped. |
| `Malformed` | The manifest/signature is structurally unusable. |

The distinction between `SignatureInvalid` (metadata tampered) and
`ContentMismatch` (media swapped) lets a reviewer say precisely *how* a record was
altered, not just that it was.

## Verifying the full capture→edit→sync chain

For the multi-step history (a capture followed by signed edits and a sync),
`Honua.Collect.Core.Provenance.ProvenanceChainVerifier` walks the hash-linked chain
and checks every step's signature, sequence, and prior-hash link, plus (optionally)
that the current bytes still match the head content hash.

> **The chain must be anchored to a trusted signer key.** Each link carries its own
> signer public key, so the signature/link checks alone prove only that the chain is
> *internally consistent* — an attacker who fabricates a complete chain over forged
> media with their **own** keypair produces an internally-consistent chain. The
> parameterless `VerifyChain(chain)` / `Verify(chain, bytes)` overloads therefore
> prove internal consistency **only** and must not be used as a standalone
> legal/compliance verdict.
>
> For an identity-bound verdict, call the overloads that take a trusted-key allowlist:
> `VerifyChain(chain, trustedSignerKeys)` (and `Verify(chain, bytes, trustedSignerKeys)`),
> which require the genesis capture's signer key to be a **registered device/authority
> key** — pass `requireEveryStepTrusted: true` to require it of every step. A chain
> whose genesis (or, when required, any) signer is outside the allowlist returns
> `UntrustedSigner` instead of `Valid`.

## Tamper-evident history

Separately, on-device actions are recorded in an append-only, **hash-chained**
audit log (`Honua.Collect.Core.Enterprise.AuditLog`): each entry's hash chains the
previous entry with the event, so any later modification or deletion of an earlier
entry breaks the chain and is detected by `AuditLog.Verify()`. Per-record edit
history (`RecordEditHistory`) additionally captures who changed what, when —
including post-sync edits — and can reconstruct any prior version.

## A submission workflow

1. Capture the photo + GPS; Collect computes the content hash and signs the
   manifest with the device key.
2. Submit the record and its signed provenance through sync.
3. A reviewer independently re-runs `ProvenanceVerifier.Verify` with the media
   bytes and the device/authority public key. A `Valid` result proves the photo
   was taken by user X, at place/time Y, on device Z, and is unaltered; any
   tampering yields `SignatureInvalid` or `ContentMismatch`.

## Current limits (tracked in #41)

Documented for honesty — these are not yet in the repository:

- **C2PA content-credential embedding.** Provenance here is a signed sidecar
  manifest, not yet a C2PA manifest embedded in the image file itself; that needs
  the `c2pa` library and image I/O.
- **Hardware-key binding.** The signing/verification core is implemented and
  tested; wiring the private key to the platform Keystore/Secure Enclave is the
  device-side step.
- **Provenance-preserving redaction** (blur faces without invalidating the
  credential) builds on C2PA and is pending the above.
