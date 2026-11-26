# End-to-end verification — emulator → server → database

Proof that a form filled in the Honua Field app on an Android emulator is
submitted over HTTP to a running Honua server and persisted as a row in
PostgreSQL — the full capture → sync → store loop.

## Published XLSForm Android gate

The [Published forms on Android workflow](../../../.github/workflows/published-android.yml) installs a standalone Debug APK on an Android API 33 emulator, builds the pinned Honua Server revision, starts PostGIS and publishes the inspection workbook. Maestro drives the native Field screens under a layer-scoped worker account.

The gate disconnects Field from the server, cold-starts the app, captures multiple choices, a calculated value, two repeat rows, a GPS fix and a photo, and reopens the draft after process death. During upload a proxy withholds the first successful server response. Field must keep the item in its outbox across a restart, retry it and show it as sent. The test then reads the feature and photo through FeatureServer and checks PostGIS for exactly one accepted receipt at the downloaded form version.

To rerun the native flow on a dedicated emulator with the Debug APK already installed and the fixture published, run from the repository root. Supply a worker key file and a separate host-side readback key file. The script removes its temporary settings and network forwarding on exit.

```bash
HONUA_E2E_REPORT_DIR=/tmp/field-android-results \
  scripts/e2e/run-published-android.sh emulator-5554 /path/to/worker-key /path/to/readback-key
```

The workflow pins Maestro and the emulator version. Its `published-android-results` artifact contains a JUnit report for each completed phase. On success, `server-readback.json` records the verified form version, feature and attachment IDs, attachment SHA-256, repeat count and accepted receipt count. A failed run includes an emulator screenshot when available. Setting `HONUA_E2E_REPORT_DIR` retains the same evidence locally.

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
- **update / delete of a non-existent feature** — rejected with the server's
  message, not a crash;
- **submit without credentials** — fails gracefully and writes nothing;
- **concurrent adds (×12)** — all persist with distinct object ids (no id
  collision or lost write);
- **concurrent updates to the same feature (×10)** — transient write contention
  is retried with backoff, so **every** submission succeeds (no spurious
  failures) and exactly one consistent row remains;
- **duplicate submissions** — create two distinct rows (the Feature Server is not
  a dedup authority; client-side `DuplicateDetector` owns suppression).

Findings the suite surfaced (and the fix): concurrent same-row updates fail
transiently under contention — the server reuses error code 1000 for both that
and "feature not found", so `GeoServicesFeatureSync` now retries with backoff,
classifying permanence by message; contended submissions all succeed. Separately,
integer attributes in the shared `features` JSONB column round-trip as strings
(asserted tolerantly; a server-side storage-mapping item).

Bring the stack up/down with `scripts/e2e/up.sh` / `scripts/e2e/down.sh`.
