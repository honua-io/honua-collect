# Honua Collect — competitive strategy & backlog map

How Honua Collect rivals **Esri ArcGIS Survey123** and **Fulcrum** (Spatial
Networks) today, and the three-horizon plan to win. This is the strategic frame
over the tactical gap list in [`BACKLOG.md`](../BACKLOG.md); each bet below is a
tracked GitHub **epic**.

> Factual note: Fulcrum is Spatial Networks' product (PE-recapitalized 2024). It
> is **not** owned by SafetyCulture — SafetyCulture is a competitor in the
> inspection space, not a parent.

## Where we stand (honest verdict)

**On logic we are ~70–80% of the way to parity and ahead in a few Pro features.
As a *shipping field app* we are not at parity yet** — the unfinished 20–30% is
the device/sensor "last mile" (real camera/GPS/map, background sync, push,
external GNSS) that the incumbents have spent a decade polishing. The
`Honua.Collect.Core`/`Presentation` layers are real and tested; much of the App
layer is "logic done, hardware binding pending." Closing that gap is Horizon 1.

**Already rivaling:** smart forms (relevance/calc/validation/repeats),
point/line/polygon + GeoJSON, offline-first store **with SQLCipher at-rest
encryption**, bidirectional sync with retry/backoff and **field-level conflict
review**, Drafts/Outbox/Sent, and — distinctively — we **speak the Esri
GeoServices protocol** (FeatureServer query/applyEdits, generateToken).

**Already ahead (Pro):** photo annotation that never mutates the original, an AI
photo/voice-to-fields framework, per-field conflict resolution, selective sync,
RBAC + tamper-evident audit log.

**Honestly behind:** high-accuracy GNSS/RTK (Survey123's field moat), the device
layer generally, on-prem maturity/ecosystem, and finished PDF/Word reporting.

## The openings (incumbent weaknesses → our roadmap)

| Incumbent | Weakness we exploit | Our answer |
|---|---|---|
| Survey123 | Fragmentation across Survey123 + Field Maps + Workforce + Dashboards + Power Automate; expensive ArcGIS user types | One unified app ([#40](https://github.com/honua-io/honua-collect/issues/40)) |
| Survey123 | Inbox breaks at scale (2k→10k refresh failures) | Resilient large-dataset editing ([#38](https://github.com/honua-io/honua-collect/issues/38)) |
| Survey123 | On-prem only via costly ArcGIS Enterprise | Open self-host, no Esri license ([#37](https://github.com/honua-io/honua-collect/issues/37)) |
| Fulcrum | **No true on-prem**; per-seat (5-user floor); separate data silo | Open self-host, Esri-compatible ([#37](https://github.com/honua-io/honua-collect/issues/37)) |
| Fulcrum | **Post-sync edit lockout** (top complaint) | Never lose an edit ([#38](https://github.com/honua-io/honua-collect/issues/38)) |
| Fulcrum | Clunky reporting/export | Bulk + AI-drafted reporting ([#39](https://github.com/honua-io/honua-collect/issues/39)) |
| Both | AI is **cloud-bound** | Offline on-device AI ([#41](https://github.com/honua-io/honua-collect/issues/41)) |
| Both | No cryptographic capture provenance | C2PA content credentials ([#41](https://github.com/honua-io/honua-collect/issues/41)) |

## Three horizons

### Horizon 1 — earn the right to compete (table stakes)
Mandatory, not innovative. Without these we aren't in the race.

- **[#35](https://github.com/honua-io/honua-collect/issues/35) Device last-mile** — wire capture widgets to real camera/GPS/map, background sync (WorkManager), push (FCM).
- **[#36](https://github.com/honua-io/honua-collect/issues/36) High-accuracy positioning** — external GNSS / RTK receivers (Survey123's moat; price of admission to survey/utility verticals).
- Plus existing area-epics: [#1](https://github.com/honua-io/honua-collect/issues/1) capture, [#2](https://github.com/honua-io/honua-collect/issues/2) gis, [#3](https://github.com/honua-io/honua-collect/issues/3) forms, [#4](https://github.com/honua-io/honua-collect/issues/4) sync, [#7](https://github.com/honua-io/honua-collect/issues/7) enterprise auth, [#8](https://github.com/honua-io/honua-collect/issues/8) sensors, [#9](https://github.com/honua-io/honua-collect/issues/9) licensing.

### Horizon 2 — be what incumbents structurally can't
Exploit the openings above.

- **[#37](https://github.com/honua-io/honua-collect/issues/37) Open & self-hostable, Esri-compatible** — the positioning moat.
- **[#38](https://github.com/honua-io/honua-collect/issues/38) Never lose an edit** — post-sync editability + offline edit history.
- **[#39](https://github.com/honua-io/honua-collect/issues/39) Reporting that doesn't suck** — bulk + AI-drafted reports/exports.
- **[#40](https://github.com/honua-io/honua-collect/issues/40) Unified field ops** — native dispatch + one-app workflow.
- Plus existing [#5](https://github.com/honua-io/honua-collect/issues/5) reporting.

### Horizon 3 — leapfrog with moats no incumbent has

- **[#41](https://github.com/honua-io/honua-collect/issues/41) Verifiable provenance** — C2PA content credentials + signed chain-of-custody. **Strongest moat candidate.**
- **[#42](https://github.com/honua-io/honua-collect/issues/42) Offline on-device AI capture** — voice & photo to fields with no signal.
- **[#43](https://github.com/honua-io/honua-collect/issues/43) Spatial computer vision** — in-field asset detection & measurement.
- **[#44](https://github.com/honua-io/honua-collect/issues/44) Edge automation runtime** — offline programmable Data Events + AI actions.
- Plus existing [#6](https://github.com/honua-io/honua-collect/issues/6) AI capture.

## Recommended wedge

Don't out-Esri Esri on GIS depth or out-feature Fulcrum across the board. **Pick
the buyer neither serves well: regulated / government / critical-infrastructure
orgs that need self-hosting, verifiable provenance, and offline AI.**

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
| [#37](https://github.com/honua-io/honua-collect/issues/37) | Open & self-hostable, Esri-compatible | 2 |
| [#38](https://github.com/honua-io/honua-collect/issues/38) | Never lose an edit | 2 |
| [#39](https://github.com/honua-io/honua-collect/issues/39) | Reporting that doesn't suck | 2 |
| [#40](https://github.com/honua-io/honua-collect/issues/40) | Unified field ops — native dispatch | 2 |
| [#41](https://github.com/honua-io/honua-collect/issues/41) | Verifiable provenance — C2PA | 3 |
| [#42](https://github.com/honua-io/honua-collect/issues/42) | Offline on-device AI capture | 3 |
| [#43](https://github.com/honua-io/honua-collect/issues/43) | Spatial computer vision | 3 |
| [#44](https://github.com/honua-io/honua-collect/issues/44) | Edge automation runtime | 3 |

See [`BACKLOG.md`](../BACKLOG.md) for the per-feature parity gap list (C/G/F/S/R/A/E/I codes).
