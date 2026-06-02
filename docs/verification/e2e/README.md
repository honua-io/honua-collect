# End-to-end verification — emulator → server → database

Proof that a form filled in the Honua Collect app on an Android emulator is
submitted over HTTP to a running Honua server and persisted as a row in
PostgreSQL — the full Survey123/Fulcrum-style capture → sync → store loop.

## Topology

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

`tests/Honua.Collect.Core.Tests/Sync/FeatureSyncE2ETests.cs` performs the same
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
- **concurrent updates to the same feature (×10)** — the server resolves the
  write collision safely: at least one write lands, every losing write is
  surfaced as a failure (never silently dropped), and exactly one consistent row
  remains;
- **duplicate submissions** — create two distinct rows (the Feature Server is not
  a dedup authority; client-side `DuplicateDetector` owns suppression).

Two findings surfaced while writing these and are encoded in the assertions:
concurrent same-row updates are conflict-rejected (not last-write-wins-all), and
integer attributes stored in the shared `features` JSONB column round-trip as
strings.

Bring the stack up/down with `scripts/e2e/up.sh` / `scripts/e2e/down.sh`.
