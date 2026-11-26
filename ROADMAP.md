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
| P0 | **Published XLSForm inspections.** Server owns import, versioning and authoritative validation. Field renders choices, repeats, expressions, location and photos, and pins the form version for each draft. [Field #169](https://github.com/honua-io/honua-field/issues/169), [Field #170](https://github.com/honua-io/honua-field/issues/170) | Publish a real workbook. Disconnect the emulator, restart Field, capture an inspection, restart again and sync. Withhold the first successful server response, restart Field and retry the persisted upload. Retrieve exactly one feature and accepted receipt for that form version. Check selected codes, calculated value, ordered repeat rows, WGS84 coordinates and photo bytes on the server. Run a field-worker account through the same flow before production release. |
| P0 | **Recover an unfinished inspection and its outbox.** Mobile supplies durable queue claims and encrypted storage. Field retains the draft, original form, destination, record ID and media references. [Mobile #421](https://github.com/honua-io/honua-mobile/issues/421), [Field #173](https://github.com/honua-io/honua-field/issues/173) | Open a v1 draft offline after v2 is published. Verify pending and acknowledged edits survive loss of connectivity and process death. Before moving Field to the shared store, upgrade an installed prior version with drafts, queued uploads and photos. Compare IDs, versions, media hashes and queue states before and after. |
| P0 | **Correct a server-rejected offline inspection.** New submissions retain the pinned package, destination, answers, repeat rows and local media for correction after an authoritative rejection. Older queued submissions without a saved editor snapshot are flagged in Records and require re-entry. | Queue an inspection offline and return an authoritative validation rejection. Restart Field, reopen the rejected inspection with its original package and media, correct the invalid answer and verify a single accepted feature and receipt. In a separate run, drop the successful server response after commit, restart Field and replay the same submission ID and payload. Verify a single feature and the original receipt. |
| P1 | **Download a work area, complete assignments and reconcile edits.** Mobile owns resumable packages, change tracking and conflict primitives. Field owns task selection, offline completion and displaying both sides of a conflict. [Mobile #413](https://github.com/honua-io/honua-mobile/issues/413), [Field #165](https://github.com/honua-io/honua-field/issues/165), [Mobile #92](https://github.com/honua-io/honua-mobile/issues/92) | Interrupt an area refresh halfway and reopen the previous usable area. Complete an assignment offline. Edit the same asset on two devices, resolve the conflict, sync again and read back the chosen values. Preserve the other edit for inspection. |
| P1 | **Capture survey-grade positions and edit geometry.** Mobile supplies receiver measurements and accuracy. Field applies project tolerances, exposes fix quality, supports snapping and records measurement provenance. [Field #36](https://github.com/honua-io/honua-field/issues/36), [Field #168](https://github.com/honua-io/honua-field/issues/168), [Field #2](https://github.com/honua-io/honua-field/issues/2) | Replay known fixes across the configured accuracy threshold, then collect a surveyed control point with a supported physical receiver. Check server geometry and fix metadata. Verify point, line and polygon edits, snapping and undo against known geometries. |
| P1 | **Keep offline maps responsive.** Mobile measures Android frame timings. Field supplies a representative downloaded work area and visible map. | On a fixed device profile and versioned dataset, perform ten map pans. Warn above 32 ms p95 or 10% janky frames and fail above 50 ms or 20%. Store the device, dataset and frame sample with the result. |
| P2 | **Collect richer evidence and link independent assets.** Field supports media on individual observations and offline record links across inspections. Server retains attachment ownership and relationships. [Field #126](https://github.com/honua-io/honua-field/issues/126) | Capture two repeat observations with different evidence, reference an existing asset from two inspections offline, sync in reverse order and verify each file hash and link on the server. |

### Competitor capabilities that inform these gates

| Workflow | Documented competitor capability | Honua acceptance criterion |
| --- | --- | --- |
| Forms on a disconnected device | Survey123 supports repeat rows in XLSForm surveys. Fulcrum can change visibility and requirements based on answers, including how hidden answers are retained. [Survey123 repeats](https://doc.arcgis.com/en/survey123/create/main/xlsformrepeats.htm) · [Fulcrum conditional logic](https://help.fulcrumapp.com/en/articles/72222-conditional-logic) | P0. Change a choice offline, verify the calculated value and newly relevant question, then reopen the draft and verify submitted repeat order. Include a fixture that toggles a question back to hidden and checks server and client agreement. |
| Offline maps and simultaneous edits | Field Maps supports preconfigured offline areas. Mergin Maps synchronizes GeoPackage changes and preserves conflict information. [Field Maps configuration](https://doc.arcgis.com/en/field-maps/ios/use-maps/configure-the-map.htm) · [Mergin Maps synchronisation](https://merginmaps.com/docs/manage/synchronisation/) | P1. Interrupt a download while a usable area exists. Edit the same feature on two disconnected devices and verify the resolved values and retained conflict details after both reconnect. |
| Precision capture and digitizing | Field Maps supports accuracy thresholds, averaging and receiver metadata. Mergin Maps supports external receivers and configurable vertex or segment snapping. [Field Maps high-accuracy collection](https://doc.arcgis.com/en/field-maps/11.5/prepare-maps/high-accuracy-data-collection.htm) · [Mergin Maps receivers](https://merginmaps.com/docs/field/external_gps/) · [Mergin Maps project preparation](https://merginmaps.com/docs/gis/features/) | P1. Measure a known control point with a physical receiver, verify its metadata on the server, and compare snapped geometry with fixture coordinates. |
| Child observations and shared assets | Fulcrum repeatable sections can store a location for each child. Record links allow independent records to be referenced across inspections. [Fulcrum repeatable sections](https://help.fulcrumapp.com/en/articles/75171-what-are-repeatable-sections) · [Fulcrum record links](https://help.fulcrumapp.com/en/articles/74922-repeatables-record-links-comparison) | P2. Check each child's identity, location and attachment ownership and resolve shared asset links when related records arrive out of order. |
