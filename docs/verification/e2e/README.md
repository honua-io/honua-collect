# End-to-end verification — emulator → server → database

Proof that a form filled in the Honua Field app on an Android emulator is
submitted over HTTP to a running Honua server and persisted as a row in
PostgreSQL — the full capture → sync → store loop.

## Published XLSForm Android gate

The [Published forms on Android workflow](../../../.github/workflows/published-android.yml) builds two signed Debug APKs, starts the pinned Honua Server revision with PostGIS and publishes the inspection workbook. Maestro drives the native Field screens on Android API 33 under a layer-scoped worker account.

The first scenario installs Field versionCode 2 from commit `ff0a2c4`, downloads the workbook, disconnects the server and saves two inspections. One stays editable and one is queued. The gate installs the candidate APK over the older app and compares the Android UID, signing certificate, record IDs, queue state, cached form version and both attachment hashes. It then temporarily removes access to the device key, taps Retry, verifies that the local database files have identical hashes and restores the key. The saved draft must reopen and the queued inspection must still be ready to send.

After clearing Field’s local data on the emulator, the gate disconnects Field from the server, cold-starts the app, captures multiple choices, a calculated value, two repeat rows, a GPS fix and a photo, and reopens the draft after process death. In CI the host publishes a newer revision while the draft is offline. The original version must still be used when the draft resumes and uploads. During upload a proxy withholds the first successful server response. Field must keep the item in its outbox across a restart, retry it and show it as sent. The test then reads the feature and photo through FeatureServer and checks PostGIS for exactly one accepted receipt at the downloaded form version.

The final scenario exercises correction after the server rejects an over-length answer. The worker reopens the inspection offline, shortens the answer and uploads again. The gate checks that the server recorded the rejection and then accepted a new submission identity at the original version, with the repeat rows and photo intact.

To rerun the dropped-response scenario on a dedicated emulator with the Debug APK already installed and the fixture published, run from the repository root. Supply a worker key file and a separate host-side readback key file. To exercise the version change locally, set `HONUA_E2E_PUBLISH_NEXT_VERSION=1` and pass the disposable fixture admin key as the third argument. The script removes its temporary settings and network forwarding on exit.

```bash
HONUA_E2E_REPORT_DIR=/tmp/field-android-results \
  scripts/e2e/run-published-android.sh emulator-5554 /path/to/worker-key /path/to/readback-key
```

The workflow pins Maestro and the emulator version. Its `published-android-results` artifact groups JUnit reports by scenario and capture phase. Compare `upgrade/before.json`, `upgrade/after.json` and `upgrade/recovered-key.json` for installed upgrade evidence. The replay and correction folders contain `server-readback.json` with the verified form version, feature and attachment IDs, attachment SHA-256, repeat count and accepted receipt count. The correction folder also contains the two submission identities in `correction-ledger.json`. A failed capture or sync run includes an emulator screenshot when available. Setting `HONUA_E2E_REPORT_DIR` retains the same evidence locally.

The [hosted Android run](https://github.com/honua-io/honua-field/actions/runs/45872622719) passed all 23 Maestro phases across the installed upgrade (7), receipt replay (7) and rejection correction (9). The [run artifact](https://github.com/honua-io/honua-field/actions/runs/45872622719/artifacts/15144523638) contains the matching upgrade snapshots, both server readbacks and the correction ledger.

## GeoServices capture topology

- **App**: `io.honua.collect` (Release APK) on a KVM-accelerated Android-35 emulator.
- **Server**: prebuilt `honua-server` image, Development env, on `:18080`, backed by
  PostGIS. Editable layer `mobile_offline_demo` / `68910` ("Offline Field Sites")
  seeded from `tests/seed/mobile-offline-demo-v1.sql` and exposed via an activated
  Metadata-v2 snapshot.
- **Transport**: `GeoServicesFeatureSync` posts the captured `FieldRecord` to the
  GeoServices Feature Server `applyEdits` endpoint (record values → attributes,
  `Location` → point geometry). The emulator reaches the host at `10.0.2.2:18080`.

## Run (one captured submission)

1. App renders the dynamic **Field Site** form (`01-form.png`).
2. Entered Site name `EMU-3649-SITE`, picked Status `New` (`02-filled.png`); Submit enables.
3. Submit → `GeoServicesFeatureSync` POSTs `applyEdits`; the app shows
   **"Synced to server — objectId 6892005"** (`03-synced.png`).
4. The row is in Postgres:

   ```
   objectid   | 6892005
   layer_id   | 68910
   geom       | POINT(-157.81 21.31)
   attributes | {"status": "new", "site_name": "EMU-3649-SITE"}
   ```

   and reads back through the Feature Server query by `site_name`.

## Automated counterpart

`tests/Honua.Field.Core.Tests/Sync/FeatureSyncE2ETests.cs` performs the same
capture → `applyEdits` → read-back assertion against a running server (opt-in via
`HONUA_E2E_SERVER` / `HONUA_E2E_APIKEY`), so the path is regression-testable
without the emulator. `GeoServicesFeatureSync` itself has unit tests for the
request shape and response parsing.

## Comprehensive scenario coverage

`FeatureSyncE2ETests` (opt-in, gated on `HONUA_E2E_SERVER`) now covers, against a
live server:

- **add** — geometry + attribute round-trip and read-back;
- **update** — attribute change reflected server-side;
- **delete** — feature removed;
- **missing feature** — an update returns the server rejection and a repeated delete succeeds idempotently;
- **submit without credentials** — fails gracefully and writes nothing;
- **concurrent adds (×12)** — all persist with distinct object ids (no id
  collision or lost write);
- **concurrent updates to the same feature (×10)** — transient write contention
  is retried with backoff, so **every** submission succeeds (no spurious
  failures) and exactly one consistent row remains;
- **independent captures with identical answers** — distinct record IDs produce two feature rows.

Findings the suite surfaced (and the fix): concurrent same-row updates fail
transiently under contention — the server reuses error code 1000 for both that
and "feature not found", so `GeoServicesFeatureSync` now retries with backoff,
classifying permanence by message; contended submissions all succeed. Separately,
integer attributes in the shared `features` JSONB column round-trip as strings
(asserted tolerantly; a server-side storage-mapping item).

All nine tests passed on Field `889b8d1` against the repository’s seeded layer 68910, Honua Server `83127df`, PostgreSQL 18.6 and PostGIS 3.6.4. This live suite exercises Field’s GeoServices client directly. The Maestro workflow above covers the installed Android app and published XLSForm path.

Bring the stack up/down with `scripts/e2e/up.sh` / `scripts/e2e/down.sh`.
