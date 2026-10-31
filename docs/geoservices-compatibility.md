# GeoServices REST compatibility matrix

This matrix summarizes the GeoServices REST capabilities implemented by Honua Collect. Entries are grounded in this repository; partial and planned work is identified explicitly.

> Legend: ✅ shipped & tested · 🟡 partial / core-done, device-binding or UI
> pending · 🔭 planned · — not applicable / not offered.
>
> Honesty note: Collect's `Honua.Collect.Core` logic and
> `Honua.Collect.Presentation` (MVVM) layers are real and unit-tested. Several
> **device "last-mile" bindings** (camera, GNSS, map surface) live in the
> `Honua.Collect.App` MAUI shell and are still being wired — those are marked 🟡
> below and tracked in [#35](https://github.com/honua-io/honua-collect/issues/35)
> / [#36](https://github.com/honua-io/honua-collect/issues/36).

## GeoServices REST feature services

Collect reads and writes GeoServices REST feature services, including services hosted by Honua Server.

| Capability | Honua Collect | Evidence |
| --- | :---: | --- |
| FeatureServer `query` (read/pull, paged) | ✅ | [`GeoServicesFeatureSync.QueryAsync`](../src/Honua.Collect.Core/Sync/GeoServicesFeatureSync.cs) — follows `resultOffset`/`resultRecordCount` + `exceededTransferLimit` |
| `applyEdits` — adds | ✅ | `SubmitAsync` |
| `applyEdits` — updates | ✅ | `UpdateAsync` |
| `applyEdits` — deletes | ✅ | `DeleteAsync` |
| `addAttachment` (upload) | ✅ | `AddAttachmentAsync` (multipart/form-data) |
| `queryAttachments` (download) | 🔭 | not implemented; only upload today |
| Token auth (`generateToken`) | ✅ | [`ServerCredentialVerifier`](../src/Honua.Collect.Presentation/Auth/ServerCredentialVerifier.cs) |
| Transient-failure retry with backoff | ✅ | [`FeatureSyncRetryPolicy`](../src/Honua.Collect.Core/Sync/GeoServicesFeatureSync.cs) (exponential + jitter) |
| Reads and writes GeoServices REST feature services | ✅ | Protocol interoperability ([#37](https://github.com/honua-io/honua-collect/issues/37)) |



## Forms

| Capability | Honua Collect | Evidence |
| --- | :---: | --- |
| Dynamic field types (text, number, choice, date, …) | ✅ | [`FormSession`](../src/Honua.Collect.Core/Field/Forms/FormSession.cs) |
| Live conditional visibility / relevance | ✅ | `FormSession` (cascading dependencies) |
| Calculated fields | ✅ | `FormSession` |
| Per-field validation + submit gating | ✅ | `FormSession` |
| Repeating groups (with min/max bounds) | ✅ | [`RepeatGroup`](../src/Honua.Collect.Core/Field/Forms/RepeatGroup.cs) |
| Single/multi-select choice lists | ✅ | form model |
| Cascading selects | ✅ | [`ChoiceCascade`](../src/Honua.Collect.Core/Field/Forms/Cascade/ChoiceCascade.cs) |
| Default-from-previous / favorites | ✅ | `FormSession` |
| XLSForm import | ✅ | [`XlsFormImporter`](../src/Honua.Collect.Core/Field/Forms/Authoring/XlsFormImporter.cs) — see [migration guide](./migration-from-external-forms.md) |

## Capture

| Capability | Honua Collect | Evidence |
| --- | :---: | --- |
| Point / line / polygon geometry capture | ✅ (logic) / 🟡 (map surface) | [`GeometryCaptureSession`](../src/Honua.Collect.Core/Field/Geometry/GeometryCaptureSession.cs); 2D map widget is app-layer ([#35](https://github.com/honua-io/honua-collect/issues/35)) |
| RFC 7946 GeoJSON geometry output | ✅ | `GeometryCaptureSession` |
| GPS averaging for higher-accuracy vertices | ✅ | [`GpsAverager`](../src/Honua.Collect.Core/Field/Geometry/GpsAverager.cs) |
| Live device GPS binding (real fixes) | 🟡 | core ready; device location provider is app-layer ([#35](https://github.com/honua-io/honua-collect/issues/35)) |
| High-accuracy external GNSS / RTK | 🔭 | planned ([#36](https://github.com/honua-io/honua-collect/issues/36)) |
| Photo / signature capture | ✅ (policy) / 🟡 (camera surface) | [`MediaCaptureField`](../src/Honua.Collect.Core/Field/Capture/MediaCaptureField.cs) enforces count/type/size; camera binding is app-layer |
| Photo annotation/markup (non-destructive) | ✅ | [`PhotoAnnotationOverlay`](../src/Honua.Collect.Core/Field/Annotation/PhotoAnnotationOverlay.cs) — original image never mutated |
| Barcode / QR decode | ✅ | see [`docs/verification/followups/`](./verification/followups/) (`08-barcode-decoded.png`) |

## Offline & sync

| Capability | Honua Collect | Evidence |
| --- | :---: | --- |
| Offline-first capture (no signal) | ✅ | local store + `FormSession` run fully offline |
| Encrypted at-rest local store | ✅ (SQLCipher) | [`SqliteRecordStore`](../src/Honua.Collect.Core/Storage/SqliteRecordStore.cs); 256-bit key in platform secure store |
| Drafts / Outbox / Sent / Conflicts boxes | ✅ | [`RecordBox`](../src/Honua.Collect.Core/Records/RecordBox.cs) |
| Bidirectional sync (push + pull) | ✅ | `GeoServicesFeatureSync` (`SubmitAsync` + `QueryAsync`) |
| Field-level conflict **detection** | ✅ | [`RecordConflictDetector`](../src/Honua.Collect.Core/Sync/RecordConflictDetector.cs) — per-field diffs |
| Field-level conflict **resolution** (merge) | ✅ | [`RecordConflict.Resolve`](../src/Honua.Collect.Core/Sync/RecordConflict.cs) (per-field keep-local/keep-server) |
| Conflict-review **UI** | 🟡 | core logic done; review screen binding pending ([#38](https://github.com/honua-io/honua-collect/issues/38)) |
| Post-sync editability (never lose an edit) | 🔭 | tracked in [#38](https://github.com/honua-io/honua-collect/issues/38) |
| Selective / partial sync | 🟡 | Pro-gated (`AdvancedSyncAndGis`); query `where`-clause filtering exists in `QueryAsync` |
| Background sync (WorkManager / push) | 🔭 | app-layer, planned ([#35](https://github.com/honua-io/honua-collect/issues/35)) |

## Attachments & media upload

| Capability | Honua Collect | Evidence |
| --- | :---: | --- |
| Attach media to a feature and upload | ✅ | `AddAttachmentAsync` → FeatureServer `addAttachment` |
| Capture metadata (location, time, redaction flag) | ✅ | [`MediaCaptureField`](../src/Honua.Collect.Core/Field/Capture/MediaCaptureField.cs) |
| Download existing attachments (`queryAttachments`) | 🔭 | not implemented |

## Export & reporting

| Capability | Honua Collect | Evidence |
| --- | :---: | --- |
| CSV export | ✅ | [`RecordExporter.ToCsv`](../src/Honua.Collect.Core/Export/RecordExporter.cs) |
| GeoJSON export (RFC 7946) | ✅ | `RecordExporter.ToGeoJson` |
| KML export (OGC 2.2) | ✅ | `RecordExporter.ToKml` |
| GeoPackage export (OGC) | ✅ | [`GeoPackageExporter`](../src/Honua.Collect.Core/Export/GeoPackageExporter.cs) |
| Shapefile export | ✅ | [`ShapefileExporter`](../src/Honua.Collect.Core/Export/ShapefileExporter.cs) |
| Per-record templated PDF/Word report | 🟡 | [`RecordReportRenderer`](../src/Honua.Collect.Core/Reports/RecordReportRenderer.cs) — Pro-gated; finished templating tracked in [#39](https://github.com/honua-io/honua-collect/issues/39) |
| Bulk export / reporting | 🟡 | Pro-gated (`ReportsAndExports`); bulk UX tracked in [#39](https://github.com/honua-io/honua-collect/issues/39) |

## Authentication & enterprise

| Capability | Honua Collect | Evidence |
| --- | :---: | --- |
| Server token sign-in (`generateToken`) | ✅ | `ServerCredentialVerifier` — password never stored |
| Bearer-token transport, session resume | ✅ | [`AuthHeaderHandler`](../src/Honua.Collect.Core/Enterprise/AuthHeaderHandler.cs) + secure-store persistence |
| Proactive token refresh | 🟡 | lifecycle supports it ([`AuthSessionManager`](../src/Honua.Collect.Core/Enterprise/AuthSessionManager.cs)) but current `generateToken` issues no refresh token |
| Role-based on-device authorization | ✅ | [`DeviceAuthorization`](../src/Honua.Collect.Core/Enterprise/DeviceAuthorization.cs) + capability map |
| Tamper-evident audit trail | ✅ | [`SqliteAuditStore`](../src/Honua.Collect.Core/Enterprise/SqliteAuditStore.cs) — encrypted, exportable |
| SSO (OIDC / SAML) | 🔭 | `AuthSession` carries the model; interactive flow planned ([#7](https://github.com/honua-io/honua-collect/issues/7)) |
| Certificate pinning (SPKI) | ✅ | [`CertificatePinning`](../src/Honua.Collect.Core/Sync/CertificatePinning.cs) — opt-in |

## Deployment & licensing posture

This is where the matrix flips in Collect's favour — the [#37](https://github.com/honua-io/honua-collect/issues/37)
moat.

| Capability | Honua Collect | Evidence |
| --- | :---: | --- |
| Source-available | ✅ (FCL-1.0-ALv2) | [LICENSE](../LICENSE) |
| Self-hostable / on-prem | ✅ | [self-hosting guide](./self-hosting.md) |
| Air-gapped capable | ✅ | offline capture + offline licence verification |
| No per-seat SaaS tax / user-type tax | ✅ | open-core editions |
| Offline licence/entitlement enforcement (no phone-home) | ✅ | [`LicenseService`](../src/Honua.Collect.Core/Licensing/LicenseService.cs) (Ed25519, embedded public key) |
| Bring-your-own identity (no vendor account) | ✅ | authenticates against your server's token endpoint |

## Migration & data portability

For import and export workflows, see the full
[migration guide](./migration-from-external-forms.md):

- **From XLSForm:** import the survey's **XLSForm** directly
  ([`XlsFormImporter`](../src/Honua.Collect.Core/Field/Forms/Authoring/XlsFormImporter.cs));
  pull existing feature-layer records over GeoServices.
- **From CSV/GeoJSON:** re-express the app's fields onto the same form model; import
  records from CSV/GeoJSON exports.
- **Out of Collect:** export to CSV, GeoJSON, KML, GeoPackage, or Shapefile — no
  lock-in.

## What is deliberately marked partial / planned (honesty ledger)

To avoid overstating:

- **Device last-mile (🟡):** real camera/GPS/map-surface bindings live in the
  MAUI app and are still being wired ([#35](https://github.com/honua-io/honua-collect/issues/35)).
  The capture **logic** (geometry sessions, media policy, GPS averaging) is done
  and tested; the hardware surfaces are not all bound yet.
- **External GNSS / RTK (🔭):** not implemented — a future capability,
  planned in [#36](https://github.com/honua-io/honua-collect/issues/36).
- **`queryAttachments` (🔭):** only attachment **upload** is implemented; reading
  existing attachments from a server is not.
- **Conflict-review UI (🟡):** field-level detection and merge are complete and
  tested in Core; the review **screen** is pending ([#38](https://github.com/honua-io/honua-collect/issues/38)).
- **Post-sync editability (🔭):** targeted ([#38](https://github.com/honua-io/honua-collect/issues/38)) precisely
  because it is a planned capability — not yet shipped.
- **Token refresh (🟡):** the lifecycle supports proactive refresh, but the
  current `generateToken` endpoint issues no refresh token, so renewal is
  re-sign-in on expiry today.
- **SSO / OIDC / SAML (🔭):** the session model exists; the interactive sign-in
  flow is planned ([#7](https://github.com/honua-io/honua-collect/issues/7)).
- **Bulk + templated PDF/Word reporting (🟡):** exporters and a per-record
  renderer exist and are Pro-gated; the finished bulk/report UX is tracked in
  [#39](https://github.com/honua-io/honua-collect/issues/39).
- **Background sync / push (🔭):** app-layer, planned ([#35](https://github.com/honua-io/honua-collect/issues/35)).

For the strategic frame behind these gaps, see
[COMPETITIVE-STRATEGY.md](./COMPETITIVE-STRATEGY.md).
</content>
