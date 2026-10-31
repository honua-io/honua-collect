# Honua Collect — competitive strategy & backlog map

A three-horizon plan for Honua Collect, grounded in current capabilities and the remaining product work. This is the strategic frame
over the tactical gap list in [`BACKLOG.md`](../BACKLOG.md); each bet below is a
tracked GitHub **epic**.

## Where we stand (honest verdict)

**On logic we are ~70–80% of the way to parity and ahead in a few Pro features.
As a *shipping field app* we are not at parity yet** — the unfinished 20–30% is
the device/sensor "last mile" (real camera/GPS/map, background sync, push,
external GNSS) that require additional implementation and polish. The
`Honua.Collect.Core`/`Presentation` layers are real and tested; much of the App
layer is "logic done, hardware binding pending." Closing that gap is Horizon 1.

**Already rivaling:** smart forms (relevance/calc/validation/repeats),
point/line/polygon + GeoJSON, offline-first store **with SQLCipher at-rest
encryption**, bidirectional sync with retry/backoff and **field-level conflict
review**, Drafts/Outbox/Sent, and support the **GeoServices REST protocol** (FeatureServer query/applyEdits, generateToken).

**Already ahead (Pro):** photo annotation that never mutates the original, an AI
photo/voice-to-fields framework, per-field conflict resolution, selective sync,
RBAC + tamper-evident audit log.

**Remaining work:** high-accuracy GNSS/RTK, device integration, self-hosting
maturity, and finished PDF/Word reporting.

## Product opportunities

| Area | Opportunity | Planned work |
|---|---|---|
| Field workflow | Bring dispatch and field capture into one workflow | Unified field operations ([#40](https://github.com/honua-io/honua-collect/issues/40)) |
| Large datasets | Improve refresh and editing at scale | Resilient large-dataset editing ([#38](https://github.com/honua-io/honua-collect/issues/38)) |
| Deployment | Improve self-hosted deployment and protocol interoperability | Open self-hosting ([#37](https://github.com/honua-io/honua-collect/issues/37)) |
| Record editing | Preserve edits after synchronization | Never lose an edit ([#38](https://github.com/honua-io/honua-collect/issues/38)) |
| Reporting | Complete bulk export and reporting workflows | Reporting improvements ([#39](https://github.com/honua-io/honua-collect/issues/39)) |
| AI | Support offline processing | Offline AI ([#41](https://github.com/honua-io/honua-collect/issues/41)) |
| Provenance | Add cryptographic capture provenance | C2PA content credentials ([#41](https://github.com/honua-io/honua-collect/issues/41)) |

## Three horizons

### Horizon 1 — earn the right to compete (table stakes)
Mandatory, not innovative. Without these we aren't in the race.

- **[#35](https://github.com/honua-io/honua-collect/issues/35) Device last-mile** — wire capture widgets to real camera/GPS/map, background sync (WorkManager), push (FCM).
- **[#36](https://github.com/honua-io/honua-collect/issues/36) High-accuracy positioning** — external GNSS / RTK receivers (a planned capability; useful for to survey/utility verticals).
- Plus existing area-epics: [#1](https://github.com/honua-io/honua-collect/issues/1) capture, [#2](https://github.com/honua-io/honua-collect/issues/2) gis, [#3](https://github.com/honua-io/honua-collect/issues/3) forms, [#4](https://github.com/honua-io/honua-collect/issues/4) sync, [#7](https://github.com/honua-io/honua-collect/issues/7) enterprise auth, [#8](https://github.com/honua-io/honua-collect/issues/8) sensors, [#9](https://github.com/honua-io/honua-collect/issues/9) licensing.

### Horizon 2 — strengthen deployment and field workflows
Deliver the planned deployment, editing, reporting, and workflow improvements.

- **[#37](https://github.com/honua-io/honua-collect/issues/37) Open & self-hostable, protocol-compatible** — the positioning moat.
- **[#38](https://github.com/honua-io/honua-collect/issues/38) Never lose an edit** — post-sync editability + offline edit history.
- **[#39](https://github.com/honua-io/honua-collect/issues/39) Reporting that doesn't suck** — bulk + AI-drafted reports/exports.
- **[#40](https://github.com/honua-io/honua-collect/issues/40) Unified field ops** — native dispatch + one-app workflow.
- Plus existing [#5](https://github.com/honua-io/honua-collect/issues/5) reporting.

### Horizon 3 — build differentiated capabilities

- **[#41](https://github.com/honua-io/honua-collect/issues/41) Verifiable provenance** — C2PA content credentials + signed chain-of-custody. **Strongest moat candidate.**
- **[#42](https://github.com/honua-io/honua-collect/issues/42) Offline on-device AI capture** — voice & photo to fields with no signal.
- **[#43](https://github.com/honua-io/honua-collect/issues/43) Spatial computer vision** — in-field asset detection & measurement.
- **[#44](https://github.com/honua-io/honua-collect/issues/44) Edge automation runtime** — offline programmable Data Events + AI actions.
- Plus existing [#6](https://github.com/honua-io/honua-collect/issues/6) AI capture.

## Recommended wedge

**Focus on regulated, government, and critical-infrastructure organizations** that need self-hosting, verifiable provenance, and offline AI.

Sequence:
1. **Finish Horizon 1** (device layer + GNSS) to be credible.
2. **Ship the Horizon 2 story** — open self-host + unified app + never-lose-data.
3. **Make Horizon 3 the headline** — cryptographic provenance + offline AI, which
   no incumbent can match without re-architecting.

## Epic index

| # | Epic | Horizon |
|---|---|---|
| [#35](https://github.com/honua-io/honua-collect/issues/35) | Device last-mile — real hardware bindings | 1 |
| [#36](https://github.com/honua-io/honua-collect/issues/36) | High-accuracy positioning — GNSS / RTK | 1 |
| [#37](https://github.com/honua-io/honua-collect/issues/37) | Open & self-hostable, protocol-compatible | 2 |
| [#38](https://github.com/honua-io/honua-collect/issues/38) | Never lose an edit | 2 |
| [#39](https://github.com/honua-io/honua-collect/issues/39) | Reporting that doesn't suck | 2 |
| [#40](https://github.com/honua-io/honua-collect/issues/40) | Unified field ops — native dispatch | 2 |
| [#41](https://github.com/honua-io/honua-collect/issues/41) | Verifiable provenance — C2PA | 3 |
| [#42](https://github.com/honua-io/honua-collect/issues/42) | Offline on-device AI capture | 3 |
| [#43](https://github.com/honua-io/honua-collect/issues/43) | Spatial computer vision | 3 |
| [#44](https://github.com/honua-io/honua-collect/issues/44) | Edge automation runtime | 3 |

See [`BACKLOG.md`](../BACKLOG.md) for the per-feature parity gap list (C/G/F/S/R/A/E/I codes).
