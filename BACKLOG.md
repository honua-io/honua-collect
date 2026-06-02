# Honua Collect — feature backlog (parity with Survey123 & Fulcrum)

Gaps relative to Esri **Survey123** and **Fulcrum**, from the capability
assessment of the Honua mobile stack. Each item is tagged with:

- **Status** — ❌ absent · ⚠️ partial (primitive/metadata exists, no shipped UX) · 🧩 framework only · 🧱 platform-neutral Core model implemented & unit-tested, device UX still pending
- **Tier** — Community (free) · Pro · Enterprise
- **Owner** — where it's built, which sets the license:
  - `collect` = this repo (ELv2)
  - `mobile` = honua-mobile primitives (Apache, consumed)
  - `sdk` = honua-sdk-dotnet (Apache, consumed)

> Capabilities already at parity (dynamic forms, validation, repeats, calculated
> fields, offline GeoPackage sync, conflict strategies, GPS/accuracy, geofencing,
> background sync, offline basemaps, offline 3D scene packages, routing) are not
> listed here — see the assessment. This file tracks only the gaps.

## Implemented so far — `Honua.Collect.Core` (platform-neutral, unit-tested)

The product "brain" the device UX binds to is landing first, since it is fully
testable without a device and unblocks every widget and screen above it:

- **`Field/Forms/FormSession`** — the stateful form runtime: live cascading
  visibility, live calculated fields, live per-field validation, submit
  readiness + SDK workflow transition, per-field media capture,
  default-from-previous seeding, and **repeatable sections** (`RepeatGroup` /
  `RepeatInstance` rows with per-row validation and record round-trip). Backs
  **C1–C6, F3, F5** plus repeats, and is the host every capture widget binds to.
- **`Records/`** — `RecordSyncState` (transport state, orthogonal to the SDK
  review workflow), `RecordBox` Drafts/Outbox/Sent classification,
  `CollectRecordEntry` upload lifecycle, and `SyncSummary`. Backs **S3, S4**.
- **`Export/RecordExporter`** — CSV + GeoJSON bulk export. Backs **R2**.
- **`Sync/RecordConflictDetector` + `RecordConflict`** — field-level diff and
  merge for the manual conflict-review screen. Backs **S1**.
- **`Sync/SelectiveSyncPlan`** — per-layer / per-area sync opt-in decision.
  Backs **S2**.
- **`Assignments/FieldAssignment` + `AssignmentInbox`** — dispatch → capture →
  submit task loop and the worker inbox. Backs **E5**.
- **`Field/RecordLinkField`** — manages a parent record's related/child record
  links. Backs **F4**.
- **`Field/Forms/FormLocalizer` + `FormTranslations`** — produces a localized
  copy of a form (labels/help/choices) for a target language, with fallback to
  authored text. Backs **F2**.
- **`Field/Forms/Authoring/XlsFormImporter`** — imports XLSForm survey/choices
  rows (types, groups/repeats, choices, required, relevant, calculate) into a
  `FormDefinition`. Backs **F1**.
- **`Field/Capture/*`** — `ICaptureHost`, `MediaCaptureField` (policy-enforcing
  photo/video/audio/signature/sketch), `BarcodeCaptureField`, and the
  `CaptureWidget` kind map. Backs **C1–C6**.
- **`Field/Geometry/*`** — `GeometryCaptureSession` (point/line/polygon, undo,
  vertex edit, GeoJSON), `GpsAverager` (G3), and `GeoSnapping` (snap-to-feature).
  Backs **G1–G3, G7**.
- **`Field/Annotation/*`** — `PhotoAnnotationOverlay` markup stored as evidence
  metadata without altering the image. Backs **C7**.
- **`Ai/*`** — voice/photo-to-fields + redaction provider contracts and the
  confidence/entitlement-gated `AiCaptureService`. Backs **A1–A3**.
- **`Reports/RecordReportRenderer`** — templated per-record Markdown reports.
  Backs **R1**.
- **`Sync/RecordConflictDetector` + `RecordConflict`**, **`Sync/SelectiveSyncPlan`**,
  **`Sync/ResumableUpload`** — manual conflict review, selective sync, and
  chunked/resumable upload. Back **S1, S2, C9**.
- **`Enterprise/*`** — `DevicePrincipal` RBAC, tamper-evident `AuditLog`,
  `AuthSession` (SSO), `ManagedAppConfig` (MDM). Back **E1–E4**.
- **`Notifications/*`** — push payloads + deterministic `PushNotificationRouter`.
  Backs **E6**.
- **`Editions/CollectEntitlements`** — runtime feature gate the product calls to
  enforce Pro/Enterprise capabilities (consumption side of the tier matrix).

These items are marked 🧱 below: Core model done, device UX still pending. What
remains is genuinely device-bound (the MAUI widget/screen/map UX), hardware
(external GNSS G5, sensors I1–I3, AR G6), an imaging library (image
resize/compression C8), or a deliberate cross-repo package cut (promoting the
repeat-storage and geometry conventions into the portable SDK record contract
for native server round-trip).

## 1. Data capture UX (biggest gap — SDK has field types + metadata, no widgets)

| # | Item | Status | Tier | Owner |
|---|---|---|---|---|
| C1 | Camera/photo capture widget (capture, retake, gallery pick) | 🧱 | Community | collect |
| C2 | Audio recording widget | 🧱 | Community | collect |
| C3 | Video capture widget | 🧱 | Community | collect |
| C4 | Signature capture pad | 🧱 | Community | collect |
| C5 | Sketch / freehand drawing widget | 🧱 | Community | collect |
| C6 | Barcode / QR scanner | 🧱 | Community | collect |
| C7 | Photo annotation / markup (Fulcrum parity) | 🧱 | Pro | collect |
| C8 | Image auto-resize/compression before upload | ❌ | Community | mobile |
| C9 | Chunked/resumable large-media upload | 🧱 | Community | mobile |

## 2. Geometry / GIS capture

| # | Item | Status | Tier | Owner |
|---|---|---|---|---|
| G1 | Point capture from map tap | 🧱 | Community | collect |
| G2 | Line/polygon drawing UI (vertex edit, undo) | 🧱 | Community | collect |
| G3 | GPS streaming/averaging for vertices | 🧱 | Community | mobile |
| G4 | Embedded 2D map rendering surface | ⚠️ | Community | collect |
| G5 | External high-accuracy GNSS receivers (Bluetooth) | ❌ | Pro | mobile |
| G6 | On-device AR scene anchoring (physical device) | 🧩 | Pro | collect |
| G7 | Snap-to-feature / topology assist | 🧱 | Pro | sdk |

## 3. Forms & authoring

| # | Item | Status | Tier | Owner |
|---|---|---|---|---|
| F1 | Form/survey builder (drag-drop or XLSForm import) | 🧱 | Pro | sdk/server |
| F2 | Multi-language / localized forms | 🧱 | Pro | sdk |
| F3 | Cascading/dependent selects beyond current visibility rules | 🧱 | Community | sdk |
| F4 | Related tables / record links UX | 🧱 | Pro | collect |
| F5 | Default-from-previous / "favorites" answer reuse | 🧱 | Community | collect |

## 4. Sync & offline (advanced)

| # | Item | Status | Tier | Owner |
|---|---|---|---|---|
| S1 | Manual conflict-review UI (ManualReview strategy is built, no UI) | 🧱 | Pro | collect |
| S2 | Selective / partial sync (per-layer, per-area opt-in) | 🧱 | Pro | collect |
| S3 | Sync status center / per-record sync state UX | 🧱 | Community | collect |
| S4 | Outbox / Sent / Drafts boxes (Survey123 parity) | 🧱 | Community | collect |

## 5. Reporting & exports (Pro)

| # | Item | Status | Tier | Owner |
|---|---|---|---|---|
| R1 | Per-record PDF/Word feature reports (templated) | 🧱 | Pro | collect |
| R2 | Bulk export (CSV/GeoJSON/GeoPackage/Shapefile) | 🧱 | Pro | collect |
| R3 | Report template designer | ❌ | Pro | collect |

## 6. AI-assisted capture (Pro — differentiator; framework exists, provider stubbed)

| # | Item | Status | Tier | Owner |
|---|---|---|---|---|
| A1 | Voice-to-fields | 🧱 | Pro | collect |
| A2 | Photo-to-fields (object/attribute extraction) | 🧱 | Pro | collect |
| A3 | Media redaction / face-blur execution | 🧱 | Pro | collect |

## 7. Enterprise auth & admin (Enterprise)

| # | Item | Status | Tier | Owner |
|---|---|---|---|---|
| E1 | SSO (OIDC/SAML) | 🧱 | Enterprise | collect |
| E2 | Role enforcement on device | 🧱 | Enterprise | collect |
| E3 | Audit logging | 🧱 | Enterprise | collect |
| E4 | MDM / white-labeling / app config | 🧱 | Enterprise | collect |
| E5 | Assignments / inbox & task dispatch | 🧱 | Pro | collect |
| E6 | Push notifications (new assignment, sync done) | 🧱 | Pro | collect |

## 8. Sensors / IoT (honua-mobile IoT module is interface-only today)

| # | Item | Status | Tier | Owner |
|---|---|---|---|---|
| I1 | Bluetooth sensor integration | ❌ | Pro | mobile |
| I2 | NFC read/write | ❌ | Pro | mobile |
| I3 | IoT sensor streaming into records | ❌ | Enterprise | mobile |

## Sequencing (proposed)

1. **Community baseline parity** — C1–C6, G1–G4, S3–S4 (makes Collect a usable Survey123/Fulcrum alternative out of the box).
2. **Licensing layer** — signed-key entitlement enforcement (gates everything below).
3. **Pro wave 1** — R1–R2 (reports/export), S1–S2 (advanced sync), A1–A3 (AI capture).
4. **Enterprise wave** — E1–E4 (auth/admin), E5–E6 (assignments/push).
5. **Hardware/specialist** — G5 (external GNSS), I1–I3 (sensors), F1–F2 (authoring/i18n).
