# Shared offline runtime adoption

Implementation tracking: [Collect #173](https://github.com/honua-io/honua-collect/issues/173)
and [mobile #421](https://github.com/honua-io/honua-mobile/issues/421).
This document defines the migration boundary and physical-phone acceptance
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

Run on the founder's physical Android phone and iPhone for each phase. Use an
installable signed build for the selected shell, a dedicated test account and
layer, and a form with a required field, a conditional field, a repeat section,
a geometry field and an attachment field. Record the app commit, build number,
published SDK versions, OS/device, server version, form version and start time.
Keep credentials and private field data out of published evidence.

1. Install the phase build. For a migration phase, upgrade a populated previous
   build without uninstalling it. Record baseline UUIDs, remote IDs, record
   states, assignment IDs and attachment counts before upgrading.
2. Sign in, download the form and the intended offline map area. Open an
   assignment and confirm its scope. Switch the phone to airplane mode.
3. Create a draft with the required field, conditional values, one repeat row,
   geometry and a photo. Save, close the app completely and reopen it offline.
   Confirm every value and the photo survive and the draft is not queued.
4. Submit that draft offline. Create and submit a second record. Close and
   reopen the app again. Confirm exactly two records remain pending, with the
   same UUIDs, values and attachment counts.
5. Reconnect and sync. Confirm exactly two remote records exist and capture
   their remote IDs. Confirm the app reports both as sent and their attachments
   can be opened. Repeat sync twice: record and attachment counts must not grow.
6. Edit one sent record offline. Change that record independently on the server.
   Reconnect and pull/sync. Review the conflict, make an explicit resolution and
   sync again. Confirm the chosen values on both sides and unchanged identity.
7. Submit another record while the network is unavailable, close the app during
   the pending operation, then reopen and reconnect. Confirm eventual delivery
   without a duplicate record and a visible recoverable failure until delivery.
8. Reopen the assignment and record history. Compare them with the baseline.
   Confirm local settings, related records and previously sent records survive
   the upgrade. Repeat the journey after a cold phone restart.

Pass only when both physical phones finish every applicable step, zero records
or attachments are lost or duplicated, identities remain stable, all migrated
baseline counts match, and no unexpected crash occurs. A phase that cannot yet
exercise a step records it as **blocked**, with the issue and missing capability;
it must not record that step as passed. Emulator and simulator results remain
separate from this acceptance result.

Encryption and interruption checks additionally run in an isolated test harness:
open the migrated database with the correct key, reject a wrong or missing key
without replacement, interrupt migration before and after destination commit,
and verify idempotent recovery. Never alter the secure-storage key on a real
phone holding the only copy of field data to simulate these failures.

## Result record

For each device record installation outcome, each numbered step's pass/fail or
blocked status, the observed local and remote counts, elapsed sync time,
redacted screenshots or logs and any follow-up issue. Include the installable
build's access location and retain the written journey with that build. A build
artifact without a completed physical installation record does not satisfy
phone acceptance.
