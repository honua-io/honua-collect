# Follow-up feature verification — Honua Collect (Android)

Four functional-gap features were added to close the distance to Survey123 /
Fulcrum parity, then wired into the MAUI app and run on the emulator (Google APIs
x86_64, API 35, KVM-accelerated). The build/run toolchain is the one documented
in [`../README.md`](../README.md).

| # | Feature | Where | Verified |
| --- | --- | --- | --- |
| 17 | **Offline persistence** — SQLite `IRecordStore` so Drafts/Outbox/Sent survive restarts | `Honua.Collect.Core/Storage/`, `CaptureStore` write-through, hydrate in `App` | **On device, end-to-end** (below) + 7 Core tests |
| 18 | **Attachment upload** — `addAttachment` multipart to the Feature Server | `GeoServicesFeatureSync.AddAttachmentAsync`, called from `MainPage` after submit | Wired; 6 Core transport tests (server round-trip needs a live feature) |
| 19 | **Form-package download** — server form package → `FormDefinition` | `Honua.Collect.Core/Forms/FormPackageClient`, "Download Latest Form" button | Button on device; 7 Core tests (round-trip needs a `FormServer` endpoint) |
| 20 | **Login / auth** — credential check → `AuthSession` | `Honua.Collect.Presentation/Auth/LoginViewModel`, "Account" tab | Page + validation on device (`01-login.png`) + 9 Presentation tests |

## Persistence — proven across an app restart

1. Captured a record (`site_name = Persist-Test-Alpha`, `status = In progress`)
   and submitted. The server was intentionally down, so it landed in the Outbox
   as **Failed — Connection failure** and was written through to SQLite.
2. The on-device database (`files/collect-records.db`) held the row verbatim:

   ```
   record_id   6966fed5c63e4309b3b9742572c123d0
   form_id     field-site
   lat/lon     21.31 / -157.81
   values_json {"site_name":"Persist-Test-Alpha","status":"in_progress"}
   sync_state  4 (Failed)      last_error  Connection failure
   ```

3. `adb shell am force-stop io.honua.collect` killed the process (clearing the
   in-memory list), then the app was relaunched cold.
4. The **Records** tab read **Drafts 0 · Outbox 1 · Sent 0** with the same record
   and its `Failed — Connection failure` state rehydrated from SQLite
   (`02-records-rehydrated.png`) — i.e. `SqliteRecordStore.LoadAllAsync`
   reconstructed the entry and its sync box on startup.

## Screenshots

| File | Demonstrates |
| --- | --- |
| `01-login.png` | The **Account** tab renders the `LoginViewModel`-bound sign-in page; tapping **Sign in** with empty fields shows live validation. |
| `02-records-rehydrated.png` | After force-stop + cold relaunch, the Outbox record is **restored from SQLite** with its failed sync state. |
| `03-home-download-form.png` | Home screen with the **Download Latest Form** action (form-package client) alongside Start New Inspection. |

## Embedded map + geometry capture (G4 / G1 / G2)

The Geometry tab now captures over a **live OpenStreetMap basemap** — no
third-party map SDK and no API key. Tiles are fetched by `OsmTileLoader` and
drawn by `SlippyMapDrawable`; all screen↔geographic mapping goes through the
unit-tested `WebMercator` projection in Core, so taps produce real coordinates
and the overlay stays registered to the basemap as you pan and zoom.

| File | Demonstrates |
| --- | --- |
| `04-map-basemap.png` | Real OSM basemap (Honolulu/Oʻahu) with the Point/Line/Polygon picker and zoom controls. |
| `05-map-polygon.png` | A 4-vertex **polygon** drawn with a translucent fill + ring, registered to the basemap; status `Polygon: 4 vertex(es) ✓`. |
| `06-map-geojson.png` | **Done** emits valid RFC 7946 GeoJSON with real coordinates (`{"type":"Polygon","coordinates":[[[-157.834…,21.332…], …]]}`) — proving tap → `WebMercator.FromScreen` → `GeometryCaptureSession` → GeoJSON. |

Point capture drops a marker at the tapped location; zooming keeps the overlay
geographically registered. 12 `WebMercator` projection tests back the math
(round-trip lat/lon↔world-pixel↔screen, known OSM tile indices, hemisphere
orientation).

## Community capture widgets (C1–C6, C8)

The dynamic form now renders a real device capture control per widget kind
(`FieldWidgetTemplateSelector` + a widget-aware capture dispatch in `FormPage`).
A bundled **Capture Kit** demo form (home → "Capture Widgets Demo") exercises all
six. No native map/scanner SDKs: photo/video use `MediaPicker`, audio uses
`Plugin.Maui.Audio` (net10-android), ink is rasterized with MAUI Graphics, and
barcodes are decoded from a still image with the pure-.NET ZXing core.

| File | Demonstrates |
| --- | --- |
| `07-capture-widgets.png` | The Capture Kit form: barcode, photo, video, audio, signature, sketch widgets, with live attachment counts. |
| `08-barcode-decoded.png` | **C6** — a scanned QR decoded to its exact payload `HONUA-ASSET-12345` into the field (`BarcodeDecoder` via ZXing). |
| `09-signature-raster.png` | **C4/C5** — freehand ink rasterized to a 294×512 PNG (the same `InkCapturePage` backs signature and sketch). |

Verified on the emulator:

- **C1 photo + C8 compression** — a picked 3000×2000 / 677 KB image was downscaled to **1600×1067 / 95 KB** JPEG before storing (`ImageCompressor`).
- **C2 audio** — recorded a clip to a WAV via `Plugin.Maui.Audio` (mic permission granted at runtime).
- **C4 signature / C5 sketch** — strokes rasterized to a white-background PNG.
- **C6 barcode** — QR decoded to `HONUA-ASSET-12345`.
- **C3 video** uses the same `MediaPicker` capture/pick + import path as the verified photo widget.

Manifest gains `CAMERA`, `RECORD_AUDIO`, and the scoped media-read permissions.

## Parity gap-fills: expression engine, bidirectional sync, offline basemaps, geo-depth

A round of agent-built, integrated work closing the Survey123/Fulcrum baseline gaps from the parity audit. Cross-repo: the forms expression engine landed in `Honua.Sdk.Field 1.2.0` (honua-sdk-dotnet) and is consumed here.

### Expression engine (SDK 1.2.0) — **device-verified**

`FormSession` now delegates calc + validation to the SDK's real expression engine (tokenizer/parser/evaluator: arithmetic, comparison, logical, `if`, dates, strings, `$field` refs) and evaluates boolean `RelevanceExpression`. The bundled **Smart Form** demo (home → "Smart Form (Expressions) Demo") proves all three on device:

| File | Demonstrates |
| --- | --- |
| `10-smart-form-expressions.png` | `Total = $quantity * $unit_price` computes **120** (real arithmetic, not the old concat/sum); the **Approver** field appears via boolean relevance `$total > 100`; and the constraint `$coupon = '' or len($coupon) = 5` blocks the invalid coupon "ABC" with its message. |

### Offline basemaps — **device-verified**

Tiles now persist to an on-disk cache (`TileCache`), and the geometry page's **⤓ Area** button prefetches the visible viewport across zoom+2 into that cache.

| File | Demonstrates |
| --- | --- |
| `11-offline-area.png` | "Offline area ready: 54 tiles cached" — 54 PNG tiles written under `files/tiles/`, so the area renders offline. |

### Bidirectional sync — wired, unit-tested

`GeoServicesFeatureSync.QueryAsync` pulls server features (paged) into `FieldRecord`s; `FeaturePullService` classifies new-vs-conflict via `RecordConflictDetector`; the Sync tab's **Pull from server** button surfaces conflicts to the review screen. **Live round-trip verified on device** against the e2e server: the app pulled all 5 seeded server features (`12-pull-roundtrip.png`); after submitting a record, changing it server-side, and pulling again, the field-level conflict (Status `new` vs `done`) surfaced in the review screen (`13-conflict-review.png`). Covered by Core + Presentation tests too.

### Geo-depth — Core/VM wired, unit-tested

Snap-to-feature and GPS averaging are surfaced through `MapCaptureViewModel`, and repeat min/max bounds are enforced in `FormSession.Validate()` (the thin on-map toggle/GPS-button UI is the remaining wiring).

Test totals after integration: **Core 226 + Presentation 38** (collect) and **108** (SDK Field).

## Pro-tier wave: reports/export, photo annotation, assignments, AI photo-to-fields

Four parallel agents added the Pro feature surface over existing Core; integrated and verified on the emulator.

| Feature | File | Result |
| --- | --- | --- |
| **Assignments inbox (E5)** | `14-assignments-inbox.png` | Inbox lists 4 demo assignments ("4 open · 1 overdue"); tapping "Open ›" starts a capture form with the assignment title prefilled into `site_name`. |
| **Photo annotation (C7)** | `15-photo-annotation.png` | After a photo capture, "Add markup?" opens an editor (Red/Yellow/Black); Save flattens the photo + strokes to a 1600×1067 PNG attachment. |
| **Reports + bulk export (R1/R2)** | — | The Export tab shows "2 record(s)"; "Export CSV" wrote `cache/field-site.csv` (header + 2 rows) and opened the native share sheet. GeoJSON + per-record Markdown report use the same path. Pro-gated via `CollectEntitlements`. |
| **AI photo-to-fields (A2)** | — | The form's "✨ AI fill from photo" button is wired to a real Anthropic vision provider (forced tool-use → `{field_id, value, confidence}`); with no key set it prompts "Set an Anthropic API key". Live extraction is one API key away. |

New tabs: **Inbox**, **Export** (under the More overflow). Test totals: Core 233 + Presentation 62 (collect). The AI provider has 7 Core tests against a stubbed Anthropic response.
