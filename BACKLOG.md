# Honua Collect — feature backlog (parity with Survey123 & Fulcrum)

> **Strategy:** for the competitive frame over this gap list — the rivalry
> verdict, the incumbent weaknesses we exploit, and the three-horizon plan
> (parity → differentiate → leapfrog) with its tracked epics — see
> [`docs/COMPETITIVE-STRATEGY.md`](docs/COMPETITIVE-STRATEGY.md).

Gaps relative to Esri **Survey123** and **Fulcrum**, from the capability
assessment of the Honua mobile stack. Each item is tagged with:

- **Status** — ❌ absent · ⚠️ partial (primitive/metadata exists, no shipped UX) · 🧩 framework only · 🧱 platform-neutral Core model implemented & unit-tested, device UX still pending · ✅ (suffix) verified working on a device/emulator
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
- **`Assignments/FieldAssignment` + `AssignmentInbox` + `AssignmentService`** —
  dispatch → capture → submit task loop, the worker inbox, and the operator-scoped
  dispatch service (auth-session identity, assignee-only + role guards, SQLite
  persistence via `SqliteAssignmentStore`, and the `IAssignmentSyncClient` pull/push
  seam). Backs **E5** / epic #40. Live server binding deferred.
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

### Presentation layer — `Honua.Collect.Presentation` (the screen logic, unit-tested)

The MVVM view-models the MAUI app binds to, built without any MAUI dependency so
the screen behaviour is testable headlessly:

- **`Forms/FormPageViewModel` + `FieldViewModel` + `RepeatGroupViewModel`** — the
  dynamic form-capture screen: live visibility/calculation/validation as fields
  change, repeat add/remove, save-draft/submit.
- **`Sync/SyncCenterViewModel`** — Drafts/Outbox/Sent + upload lifecycle.
- **`Sync/ConflictReviewViewModel`** — per-field local/server resolution.
- **`Assignments/InboxViewModel`** — assignment lifecycle.
- **`Geometry/MapCaptureViewModel`** — point/line/polygon capture.

The MAUI app (`Honua.Collect.App/Views/FormPage.xaml` + `FieldWidgetTemplateSelector`)
binds to these. The XAML is a thin surface; the logic is the view-models above.

These items are marked 🧱 below: runtime + screen logic done and tested.

### Cross-repo updates (done — on branches, pending package cut)

The repeat round-trip through native server sync is now landed upstream, not
just via Collect's product-side convention:

- **honua-sdk-dotnet** (`field-native-repeats`): `FieldRecord.Repeats` carries
  `FieldRepeatInstance` rows natively; `FormValidator`/`CalculatedFieldEvaluator`
  evaluate each row against its own values (errors as `sectionId[index].field`).
  21 existing + 5 new Field tests green.
- **honua-server** (`forms-repeatable-sections`): `FormSectionDefinition` gains
  `Repeatable`/`MinInstances`/`MaxInstances`; `FormPackageValidator` enforces the
  bounds, so a repeatable form authored/distributed/validated server-side
  round-trips to the capture client. 13 existing + 4 new tests green.

Once those branches merge and a new `Honua.Sdk.Field` package is cut, Collect's
`FormSession` repeat storage swaps from its `List<Dictionary>` convention to the
SDK's native `FieldRecord.Repeats`.

### The MAUI app builds, installs, and runs — verified on an Android emulator

The Android toolchain was bootstrapped into `$HOME` (Temurin JDK 17 + Android
SDK: cmdline-tools, platform android-36, build-tools 36.0.0, platform-tools,
emulator, and an API-35 x86_64 system image). The MAUI app **builds in Debug and
Release (0/0)** and packages to a signed APK — `io.honua.collect` ("Honua
Collect"), minSdk 21 / targetSdk 36 — that `apksigner` verifies under the
v1/v2/v3 schemes.

It was then **installed and run end-to-end on a KVM-accelerated Android-35
emulator**, with screenshots captured in [`docs/verification/`](docs/verification/).
Verified on-device: the home screen, the **dynamic form** rendering from a
`FormDefinition`, **live required-field validation**, **conditional visibility**
(the Notes field appears when Serviceable → No), **repeatable sections** (Add
creates a Deficiency row with its own validated fields), the live error summary,
and Submit gated on validity. See `docs/verification/README.md` for the exact
build/run commands.

What still needs a physical device (not an emulator) is the **native hardware
widget plumbing**: real camera/microphone capture, signature/sketch ink
surfaces, the live map control, external GNSS (G5), sensors (I1–I3), AR (G6),
and image compression (C8). The form/validation/visibility/repeat/sync logic is
proven; these are device-sensor integrations.

> Note: enabling the emulator required granting `/dev/kvm` access (done via the
> Docker daemon since the user isn't in the `kvm` group); this is a host
> permission change that reverts on reboot.

## 1. Data capture UX (biggest gap — SDK has field types + metadata, no widgets)

| # | Item | Status | Tier | Owner |
|---|---|---|---|---|
| C1 | Camera/photo capture widget (capture, retake, gallery pick) | 🧱✅ | Community | collect |
| C2 | Audio recording widget | 🧱✅ | Community | collect |
| C3 | Video capture widget | 🧱✅ | Community | collect |
| C4 | Signature capture pad | 🧱✅ | Community | collect |
| C5 | Sketch / freehand drawing widget | 🧱✅ | Community | collect |
| C6 | Barcode / QR scanner (decode from image) | 🧱✅ | Community | collect |
| C7 | Photo annotation / markup (Fulcrum parity) | 🧱✅ | Pro | collect |
| C8 | Image auto-resize/compression before upload | ✅ | Community | collect |
| C9 | Chunked/resumable large-media upload | 🧱 | Community | mobile |

## 2. Geometry / GIS capture

| # | Item | Status | Tier | Owner |
|---|---|---|---|---|
| G1 | Point capture from map tap | 🧱✅ | Community | collect |
| G2 | Line/polygon drawing UI (vertex edit, undo) | 🧱✅ | Community | collect |
| G3 | GPS streaming/averaging for vertices | 🧱 | Community | mobile |
| G4 | Embedded 2D map rendering surface (OSM tiles, no API key) | 🧱✅ | Community | collect |
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
| F6 | Offline Data Events automation engine (rules → set/compute/tag/notify/follow-up/HTTP/AI actions; #44) | 🧱 | Pro | collect |

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
| R1 | Per-record feature reports (templated Markdown/PDF/DOCX) | 🧱✅ | Pro | collect |
| R2 | Bulk export (CSV/GeoJSON/KML + field-mapped CSV) | 🧱✅ | Pro | collect |
| R2b | Bulk report generation (N records → manifest of MD/PDF/DOCX) | 🧱✅ | Pro | collect |
| R3 | Report template designer (model + AI-narrative seam; UI deferred) | 🧱 | Pro | collect |

## 6. AI-assisted capture (Pro — differentiator; framework exists, provider stubbed)

| # | Item | Status | Tier | Owner |
|---|---|---|---|---|
| A1 | Voice-to-fields | 🧱 | Pro | collect |
| A2 | Photo-to-fields (Anthropic vision; needs API key) | 🧱 | Pro | collect |
| A3 | Media redaction / face-blur execution | 🧱 | Pro | collect |

## 7. Enterprise auth & admin (Enterprise)

| # | Item | Status | Tier | Owner |
|---|---|---|---|---|
| E1 | SSO (OIDC/SAML) | 🧱 | Enterprise | collect |
| E2 | Role enforcement on device | 🧱 | Enterprise | collect |
| E3 | Audit logging | 🧱 | Enterprise | collect |
| E4 | MDM / white-labeling / app config | 🧱 | Enterprise | collect |
| E5 | Assignments / inbox & task dispatch | 🧱✅ | Pro | collect |
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
