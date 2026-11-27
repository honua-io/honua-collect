# Shared offline runtime adoption

Implementation tracking: [Field #173](https://github.com/honua-io/honua-field/issues/173)
and [mobile #421](https://github.com/honua-io/honua-mobile/issues/421).
This document defines the migration boundary and installation acceptance
journey. It is a test script, not evidence that the migration has shipped.

## Capability ownership

| Capability | Owner | Verification |
| --- | --- | --- |
| Portable XLSForm conversion, field schemas, validation and expression semantics | `honua-sdk-dotnet` provides the shared form model and evaluator. Honua Server owns publication and submission acceptance. Field renders the published version and preserves it with each draft. | Run the same fixtures through workbook conversion and published-form evaluation. Cover repeat scope, multi-select `selected()`, empty answers, division and modulo. |
| Feature queries, edits, attachment requests, sync contracts and the reconciliation algorithm | `honua-sdk-dotnet` owns the protocol clients, per-edit results, and sync engine. | Against Honua Server, distinguish an explicit rejection from acceptance followed by a lost response. Verify remote feature and attachment counts after retry. |
| Encrypted GeoPackage storage and device lifecycle | `honua-mobile` implements the journal, checkpoints and local transactions, integrates with platform key storage, and schedules sync on connectivity changes. | Reopen after process death, reject a wrong key without replacing the database, and interrupt replay between server acceptance and durable local acknowledgement. |
| Phone and tablet capture, record identity and operator decisions | Field owns the draft's pinned form, local UUID to remote object ID association, outbox presentation, conflict choices, and map editor interactions. | Exercise the installed app at phone and tablet sizes. Save offline, restart, reconnect, and verify one server record and one copy of each photo. Record the device type and server version with the result. |
| Offline assigned work | The [SDK task client](https://github.com/honua-io/honua-sdk-dotnet/pull/596) sends scoped commands and reads typed receipts. The [Mobile adapter](https://github.com/honua-io/honua-mobile/pull/456) caches assignments and persists command IDs and expected versions. The [Field pilot](https://github.com/honua-io/honua-field/pull/203) connects the active worker's session to that adapter and presents their assignments. | With two workers, drop the first successful response, reopen the store, and check that retry produces one server receipt. Switch accounts while a refresh is in flight and verify the previous worker's pending command stays with that account. |

### Migration order

1. **Protect pending records during storage migration.** Field currently uses
   [SqliteRecordStore](../src/Honua.Field.Core/Storage/SqliteRecordStore.cs).
   Switch its persistence adapter only after the encrypted upgrade and
   interrupted-replay checks below pass on populated data.
   [Field #173](https://github.com/honua-io/honua-field/issues/173)
2. **Consolidate feature and attachment transport.** Replace hand-built requests in
   [GeoServicesFeatureSync](../src/Honua.Field.Core/Sync/GeoServicesFeatureSync.cs)
   with typed SDK operations. Preserve the distinction between a rejected add
   and one with an unknown outcome. Field decides how the worker verifies an
   uncertain upload before sending again.
   [SDK GlobalID support](https://github.com/honua-io/honua-sdk-dotnet/pull/601),
   [Field #209](https://github.com/honua-io/honua-field/issues/209) and
   [Field #214](https://github.com/honua-io/honua-field/issues/214)
3. **Adopt shared expression evaluation.** XLSForm authoring uses the
   [SDK workbook converter](https://github.com/honua-io/honua-sdk-dotnet/blob/trunk/src/Honua.Sdk.Field/Forms/Authoring/XlsFormConverter.cs).
   Compare Field's [published-expression adapter](../src/Honua.Field.Core/Forms/PublishedXlsFormExpressions.cs)
   with the SDK evaluator on the same answers, including restored drafts and
   repeat rows. Switch callers once the pinned SDK handles each rule. Field
   keeps widget selection and draft display.
   [SDK expression support](https://github.com/honua-io/honua-sdk-dotnet/pull/603) and
   [Field #169](https://github.com/honua-io/honua-field/issues/169)
4. **Reuse geometry primitives.** When snapping is enabled, the capture session
   calls [GeoSnapping](../src/Honua.Field.Core/Field/Geometry/GeoSnapping.cs).
   Compare its vertex and edge results with `Honua.Sdk.Geometry` using explicit
   coordinate systems, metre tolerances and equidistant targets. The separate
   [PlanarTopology](../src/Honua.Field.Core/Field/Geometry/PlanarTopology.cs)
   has only test callers in this repository. Mobile supplies device positions.
   Field chooses the candidate targets and tolerance for the active tool and owns undo.
   [Field #223](https://github.com/honua-io/honua-field/issues/223)

Field also owns the decision to use the JavaScript map renderer for capture.
Benchmark the offline editing flow on both phone and tablet before switching
renderers, as tracked in [Field #168](https://github.com/honua-io/honua-field/issues/168).
The reusable browser embed has a separate
[transfer to the JavaScript SDK](https://github.com/honua-io/honua-mobile/issues/425).

## Adapter boundary

Preserve the app’s record lifecycle and presentation contracts when adopting
published `Honua.Mobile.Offline` packages behind its storage interfaces. Do not introduce another HTTP upload loop, retry scheduler or store.
The app supplies the native SQLite provider and obtains its encryption key from
platform secure storage before opening the SDK database. A missing or incorrect
key must fail closed without replacing the existing database.

| App state | Migration requirement |
| --- | --- |
| Record UUID and remote object ID | Preserve both; store an explicit association. Never truncate or hash a UUID into the SDK's numeric feature ID. |
| Draft values, form version and geometry | Reopen unchanged without creating an upload operation. |
| Pending edits and attachment outbox | Import stable operation identifiers and persist data plus queue changes atomically. |
| In-progress uploads | Recover through the SDK lease mechanism after process death. |
| Sent records | Preserve remote identity; editing creates an update rather than another insert. |
| Failed and conflicted records | Preserve failure details and the local/server comparison needed for explicit resolution. |
| Assignments, history, related records and preferences | Inventory and preserve these app-owned records in the shared store; do not delete their source tables while they are still read. |

Before switching the app, the published SDK must support encryption with one
native provider, atomic record/queue writes and durable acknowledgement of
server-assigned identifiers. A successful upload cannot remove the sole queued
copy before its receipt and identity association are durable. An interrupted
migration must resume without duplicate operations or records. Keep the source
database intact until the destination has been reopened and every identifier,
state and attachment reference has been checked. Use an explicit migration
version and transaction boundaries; do not infer completion from file existence.

## Weekly installation and scripted journey

Run each phase on an Android emulator and a hosted iOS simulator. Field #173 also requires an installation and journey on the founder’s phones. Track those outcomes separately. Record the emulator image, API level and host, or the simulator device, OS and hosted runner. Use an installable build for the selected shell, a dedicated test account and
layer, and a form with a required field, a conditional field, a repeat section,
a geometry field and an attachment field. Record the app commit, build number,
published SDK versions, OS/device, server version, form version and start time.
Keep credentials and private field data out of published evidence.

1. Install the phase build. For a migration phase, upgrade a populated previous
   build without uninstalling it. Record baseline UUIDs, remote IDs, record
   states, assignment IDs and attachment counts before upgrading.
2. Sign in, download the form and the intended offline map area. Open an
   assignment and confirm its scope. Disable network access on the device or simulator.
3. Create a draft with the required field, conditional values, one repeat row,
   geometry and a photo. Save, close the app completely and reopen it offline.
   Confirm every value and the photo survive and the draft is not queued.
4. Submit that draft offline. Create and submit a second record; record both
   UUIDs as this run's cohort. Close and
   reopen the app again. Confirm both cohort records remain pending, with the
   same UUIDs, values and attachment counts; reconcile other records separately.
5. Reconnect and sync. Confirm exactly one remote record exists for each cohort
   UUID and capture their remote IDs. Confirm the app reports both as sent and their attachments
   can be opened. Repeat sync twice: cohort record and attachment counts must not grow.
6. Edit one sent record offline. Change that record independently on the server.
   Reconnect and pull/sync. Review the conflict, make an explicit resolution and
   sync again. Confirm the chosen values on both sides and unchanged identity.
7. Submit another record while the network is unavailable, close the app during
   the pending operation, then reopen and reconnect. Confirm eventual delivery
   without a duplicate record and a visible recoverable failure until delivery.
8. Reopen the assignment and record history. Compare them with the baseline.
   Confirm local settings, related records and previously sent records survive
   the upgrade. Repeat the journey after a cold device or simulator restart.

Pass only when Android and iOS finish every applicable step, zero records
or attachments are lost or duplicated, identities remain stable, all migrated
baseline counts match, and no unexpected crash occurs. A phase that cannot yet
exercise a step records it as **blocked**, with the issue and missing capability;
it must not record that step as passed. Identify whether each result came from
an emulator, simulator or physical phone.

Encryption and interruption checks additionally run in an isolated test harness:
open the migrated database with the correct key, reject a wrong or missing key
without replacement, interrupt migration before and after destination commit,
and verify idempotent recovery. Interrupt active uploads at two controlled
checkpoints: after lease acquisition, and after server acceptance but before
local acknowledgement persistence. Use a disposable database and a controlled
server/proxy that can pause responses; record checkpoint logs and terminate the
app process. Reopen and sync until the lease is recovered. Confirm each UUID has
exactly one remote record, the remote ID persists and the queue drains without
attachment duplication. If checkpoint instrumentation is unavailable, record
these cases as blocked rather than substituting an offline restart. Never alter the secure-storage key on a real
phone holding the only copy of field data to simulate these failures.

## Founder phone installation

Use the previous and candidate builds of `io.honua.collect` (the installed app is named Honua Field). Back up any existing field data and use a dedicated test account. Install the previous build, populate the baseline in step 1, and install the candidate over it with the app closed.

- **Android.** Use `apksigner verify --print-certs` on both APKs and confirm the signer matches. Install the candidate with `adb -s "$ANDROID_SERIAL" install -r "$CANDIDATE_APK"`. Independently generated CI debug keys can differ. For a debug upgrade, build both APKs with the same keystore.
- **iPhone.** Use device builds signed for the same team and app identifier and provisioned for the founder’s phone. On a paired development Mac, extract the candidate IPA with `ditto -x -k "$CANDIDATE_IPA" "$EXTRACT_DIR"` into a new directory. Install the extracted app with `xcrun devicectl device install app --device "$DEVICE" "$EXTRACT_DIR"/Payload/*.app`. Simulator IPAs run only on simulator targets.

Complete steps 2–8 and record the previous and candidate versions, artifact digests, device model, OS, date and receipt IDs. Keep device serials and account credentials private. The [Device Farm smoke](verification/e2e/README.md#aws-device-farm-native-smoke) verifies Records and geometry on the chosen model. The [physical Android upgrade](verification/e2e/README.md#aws-physical-android-in-place-update) checks the installed version, app UID and queued record on one phone before and after updating.

## Result record

For each device record installation outcome, each numbered step's pass/fail or
blocked status, the observed local and remote counts, elapsed sync time,
redacted screenshots or logs and any follow-up issue. Include the installable
build's access location and retain the written journey with that build. A build
artifact without a completed installation and journey record does not satisfy
acceptance.
