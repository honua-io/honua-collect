# Honua Field — architecture & roadmap

## Repository strategy (decided)

Honua Field is a **single product repo**. We are not splitting the field-
collection product across `honua-mobile` and `honua-field`.

- **`Honua.Sdk.*`** (`honua-io/honua-sdk-dotnet`) — platform-neutral SDK. Stays
  **Apache-2.0**, consumed here as versioned NuGet packages. This is the genuine
  shared foundation and is *not* absorbed.
- **`honua-mobile`** — slims toward **lower-level mobile primitives** (transport,
  GeoPackage storage, device location, native bindings). Field consumes these
  as packages (`Honua.Mobile.Sdk`, `Honua.Mobile.Offline`, `Honua.Mobile.Maui`).
- **Mobile field-collection product layer** (today `Honua.Mobile.Field` /
  `Honua.Mobile.FieldCollection`) — **consolidated into this repo (Apache-2.0)**. Third-
  party Apache portions retain their notices.

## Migration plan: port the field layer into Field

1. **Scaffold** — repo foundation, `Honua.Field.Core`, tests, MAUI app shell.
2. **Port `Honua.Mobile.Field` adapters** → `Honua.Field.Core` (forms runtime,
   capture workflow, validation glue, media attachment model), consuming the
   SDK's field contracts directly instead of the mobile package.
3. **Port `Honua.Mobile.FieldCollection.Core`** app services (assignments,
   exports, drafts) → Field.
4. **Slim `honua-mobile`** — drop the migrated surface; keep primitives only.
5. **Build the baseline UX** in `Honua.Field.App` (forms, camera,
   geometry capture, offline sync status).

## Every feature is free

There are no in-app editions or license checks: every capability ships to every
user. Paid value lives on Honua Server (licensed server-side) and in support.

## Priorities for field collection

The release gates below pair a mobile SDK capability with the field workflow that exercises it. The Android reference workflow uses an installed Field APK, a published XLSForm and a disposable Honua Server with PostGIS.

| Priority | Capability and ownership | Acceptance gate |
| --- | --- | --- |
| P0 | **Published XLSForm inspections.** Server owns import, versioning and authoritative validation. Field renders choices, repeats, expressions, location and photos, and pins the form version for each draft. [Field #169](https://github.com/honua-io/honua-field/issues/169), [Field #170](https://github.com/honua-io/honua-field/issues/170) | Publish a real workbook. Disconnect the emulator, restart Field, capture an inspection, restart again and sync twice. Retrieve exactly one feature and accepted receipt for that form version. Check selected codes, calculated value, ordered repeat rows, WGS84 coordinates and photo bytes on the server. Run a field-worker account through the same flow before production release. |
| P0 | **Recover an unfinished inspection and its outbox.** Mobile supplies durable queue claims and encrypted storage. Field retains the draft, original form, destination, record ID and media references. [Mobile #421](https://github.com/honua-io/honua-mobile/issues/421), [Field #173](https://github.com/honua-io/honua-field/issues/173) | Open a v1 draft offline after v2 is published. Verify pending and acknowledged edits survive loss of connectivity and process death. Before moving Field to the shared store, upgrade an installed prior version with drafts, queued uploads and photos. Compare IDs, versions, media hashes and queue states before and after. |
| P1 | **Download a work area, complete assignments and reconcile edits.** Mobile owns resumable packages, change tracking and conflict primitives. Field owns task selection, offline completion and displaying both sides of a conflict. [Mobile #413](https://github.com/honua-io/honua-mobile/issues/413), [Field #165](https://github.com/honua-io/honua-field/issues/165), [Mobile #92](https://github.com/honua-io/honua-mobile/issues/92) | Interrupt an area refresh halfway and reopen the previous usable area. Complete an assignment offline. Edit the same asset on two devices, resolve the conflict, sync again and read back the chosen values. Preserve the other edit for inspection. |
| P1 | **Capture survey-grade positions and edit geometry.** Mobile supplies receiver measurements and accuracy. Field applies project tolerances, exposes fix quality, supports snapping and records measurement provenance. [Field #36](https://github.com/honua-io/honua-field/issues/36), [Field #168](https://github.com/honua-io/honua-field/issues/168), [Field #2](https://github.com/honua-io/honua-field/issues/2) | Replay known fixes across the configured accuracy threshold, then collect a surveyed control point with a supported physical receiver. Check server geometry and fix metadata. Verify point, line and polygon edits, snapping and undo against known geometries. |
| P1 | **Keep offline maps responsive.** Mobile measures Android frame timings. Field supplies a representative downloaded work area and visible map. | On a fixed device profile and versioned dataset, perform ten map pans. Warn above 32 ms p95 or 10% janky frames and fail above 50 ms or 20%. Store the device, dataset and frame sample with the result. |
| P2 | **Collect richer evidence and link independent assets.** Field supports media on individual observations and offline record links across inspections. Server retains attachment ownership and relationships. [Field #126](https://github.com/honua-io/honua-field/issues/126) | Capture two repeat observations with different evidence, reference an existing asset from two inspections offline, sync in reverse order and verify each file hash and link on the server. |

### Competitor capabilities that inform these gates

- Esri Field Maps lets a project specify accuracy and confidence, average fixes and retain receiver metadata. Honua's GNSS gate therefore includes a physical receiver and server readback. [Field Maps high-accuracy collection](https://doc.arcgis.com/en/field-maps/11.5/prepare-maps/high-accuracy-data-collection.htm)
- Fulcrum supports both child observations within one inspection and independent records shared across inspections. Test each relationship separately, including evidence attached to the correct observation. [Fulcrum repeatables and record links](https://help.fulcrumapp.com/en/articles/74922-repeatables-record-links-comparison)
- Mergin Maps synchronizes GeoPackage changes and retains conflict information. Its workflow provides a benchmark for interrupted sync and recovering concurrent edits. [Mergin Maps synchronisation](https://merginmaps.com/docs/manage/synchronisation/)
