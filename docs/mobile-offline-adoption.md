# Shared offline runtime adoption

## Offline basemap coverage

In geometry capture, frame the work area before selecting **Download area** on a
source whose provider authorizes prefetch. Set the source URL in
`OsmTileUrl.Template` and the adjacent `AllowOfflinePrefetch` to `true` before
building. Each alternate URL template uses its own cache. The plan covers the visible extent at the
current zoom and up to two finer levels, within the tile cap. For the public
OpenStreetMap source, **Check offline area** verifies the existing cache at the
current zoom. Advance your map views normally to populate that cache.

Readiness refers to the checked extent and zoom levels at the time of the
check. Cancelled or incomplete downloads keep the usable tiles. Retry the
same area to reuse those tiles, then verify the intended map view with
connectivity disabled.

Implementation tracking: [Field #173](https://github.com/honua-io/honua-field/issues/173)
and [mobile #421](https://github.com/honua-io/honua-mobile/issues/421).
This document defines the migration boundary and installation acceptance
journey. It is a test script, not evidence that the migration has shipped.

## Adapter boundary

Keep the tested form runtime, record lifecycle and presentation contracts. Put
adapters behind those contracts that use published `Honua.Mobile.Offline`
packages. Do not introduce another HTTP upload loop, retry scheduler or store.
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

## Assigned-work pilot

Build with `-p:EnableAssignedWorkPilot=true` to compile the assigned-work lifecycle
integration. Both build modes use the same combined Mobile and SDK package
versions. Only pilot builds add the assigned-work package and register the
assigned-work runtime. The pilot adds no Today screen.

The hosted validation feed verifies the immutable archives and packages recorded
in `.github/actions/mobile-validation-feed/artifacts.json`. These archives
expire. Replace both package pins with approved published versions and remove
the validation feed before rollout. The Android published-form upgrade gate
builds the pilot-enabled candidate against the unchanged historical APK.

The pilot restores the saved authenticated session after encrypted storage opens.
Use the canonical service ID from server metadata and the authenticated user ID
that matches the server's task assignee handle. Assignment snapshots and pending commands are separated by server, service and user. Offline
reads use the stored snapshot. Startup, resume, connectivity and identity changes
trigger replay and a fresh paginated download when online. An account switch
cancels the previous sync. Replays retain the original command IDs and expected
versions, and persist receipts or terminal conflict evidence in the account's
encrypted store.

The host integration tests exercise the versioned packages and encrypted store
with controlled HTTP responses. A pilot rollout also requires those dependencies
on a trusted feed and verification with a deployed server and installed app.
