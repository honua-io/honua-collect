# End-to-end verification — emulator → server → database

Proof that a form filled in the Honua Field app on an Android emulator is
submitted over HTTP to a running Honua server and persisted as a row in
PostgreSQL — the full capture → sync → store loop.

## Published XLSForm Android gate

The [Published forms on Android workflow](../../../.github/workflows/published-android.yml) builds two signed Debug APKs, starts the pinned Honua Server revision with PostGIS and publishes the inspection workbook. Maestro drives the native Field screens on Android API 33 under a layer-scoped worker account.

The first scenario installs the .NET 10 Field versionCode 3 from commit `fb16d0b`, downloads the workbook, disconnects the server and saves two inspections. One stays editable and one is queued. The gate installs the .NET 11 versionCode 4 APK over the older app and compares the Android UID, signing certificate, record IDs, queue state, cached form version and both attachment hashes. It then temporarily removes access to the device key, taps Retry, verifies that the local database files have identical hashes and restores the key. The saved draft must reopen and the queued inspection must still be ready to send.

After clearing Field’s local data on the emulator, the gate disconnects Field from the server, cold-starts the app, captures multiple choices, a calculated value, two repeat rows, a GPS fix and a photo, and reopens the draft after process death. In CI the host publishes a newer revision while the draft is offline. The original version must still be used when the draft resumes and uploads. During upload a proxy withholds the first successful server response. Field must keep the item in its outbox across a restart, retry it and show it as sent. The test then reads the feature and photo through FeatureServer and checks PostGIS for exactly one accepted receipt at the downloaded form version.

The final scenario exercises correction after the server rejects an over-length answer. The worker reopens the inspection offline, shortens the answer and uploads again. The gate checks that the server recorded the rejection and then accepted a new submission identity at the original version, with the repeat rows and photo intact.

To rerun the dropped-response scenario on a dedicated emulator with the Debug APK already installed and the fixture published, run from the repository root. Supply a worker key file and a separate host-side readback key file. To exercise the version change locally, set `HONUA_E2E_PUBLISH_NEXT_VERSION=1` and pass the disposable fixture admin key as the third argument. The script removes its temporary settings and network forwarding on exit.

```bash
HONUA_E2E_REPORT_DIR=/tmp/field-android-results \
  scripts/e2e/run-published-android.sh emulator-5554 /path/to/worker-key /path/to/readback-key
```

The workflow pins Maestro and the emulator version. Its `published-android-results` artifact groups JUnit reports by scenario and capture phase. Compare `upgrade/before.json`, `upgrade/after.json` and `upgrade/recovered-key.json` for installed upgrade evidence. The replay and correction folders contain `server-readback.json` with the verified form version, feature and attachment IDs, attachment SHA-256, repeat count and accepted receipt count. The correction folder also contains the two submission identities in `correction-ledger.json`. A failed capture or sync run includes an emulator screenshot when available. Setting `HONUA_E2E_REPORT_DIR` retains the same evidence locally.

The installed upgrade and receipt replay use a phone window (411 × 891 dp). Rejection and correction use a tablet window (800 × 1280 dp). The receipt and correction scenarios each start with an empty local store.

The geometry check draws a three-vertex polygon on a 360 × 720 dp phone window. It rotates the phone, resizes to an 800 × 1280 dp tablet window, rotates the tablet and returns to the original phone window. At each size it checks that the editing controls are visible and the exported GeoJSON has exactly the original coordinates. It also checks Undo and the home form column width. The `adaptive` artifact folder contains screenshots and the bounds of each control.

The GPS geometry check uses a fresh local store on the installed candidate. It grants Android location permission, injects fresh location fixes through the emulator console, waits for the three-fix average, and verifies that Done returns a Point within 0.0001° of the injected coordinates. The `gps-geometry` artifact folder contains the screenshot and returned GeoJSON. This exercises the Android location and capture flow with mock fixes. Unit tests exercise stale and inaccurate fixes.

Run the same check after installing the signed candidate APK on an unlocked disposable emulator:

```bash
python3 scripts/e2e/check-gps-geometry.py emulator-5554 ./gps-geometry-report
```

A separate local Android check typed `field27` into the sample inspection’s Site name. The unsaved answer stayed visible through phone landscape, tablet portrait, tablet landscape and the return to phone portrait. At 240 dpi the entry was 492 pixels wide on the portrait phone and 1,032 pixels wide in both tablet orientations.

A local Records check loaded 175 drafts, five outbox records and five sent records. On a portrait phone and a landscape tablet, the tester scrolled through all three groups and opened the edit history for the last sent record.

The [hosted Android run](https://github.com/honua-io/honua-field/actions/runs/45895394432) passed the installed .NET 10 to .NET 11 upgrade (versionCode 3 to 4), encryption-key recovery, receipt replay, rejection correction and adaptive geometry checks. For each submission scenario, Honua Server returned exactly one feature, one accepted receipt, two repeat rows and the expected attachment checksum. The [run artifact](https://github.com/honua-io/honua-field/actions/runs/45895394432/artifacts/15155210669) contains the matching upgrade snapshots, device screenshots, both server readbacks and the correction ledger.

## Ordinary feature photos on Android

The installed app picks a fixture image from the Android gallery in airplane mode, submits the inspection to the outbox and restarts Field. Before reconnecting, the gate verifies the server has no matching feature. After sync, the gate compares the server attachment against the JPEG saved by Field. It restarts Field again, verifies the record is Sent and checks that the feature and attachment IDs stay the same.

All four Maestro phases passed with Field `eae4e68` and Honua Server `1c59eb2`. The layer-scoped worker synced one feature and one attachment. The downloaded JPEG matched all 952 bytes of Field’s private copy. The artifact contains the Field, Server and harness revisions, installed APK checksum and JUnit results. [Workflow run](https://github.com/honua-io/honua-field/actions/runs/46118802646) · [Hosted artifact](https://github.com/honua-io/honua-field/actions/runs/46118802646/artifacts/15252944002) · [Server readback](ordinary-photo-android.json)

The [published Android workflow](../../../.github/workflows/published-android.yml) builds the Debug APK, installs Maestro and runs the [gate](../../../.github/workflows/run-published-android-gate.sh). The gate starts the pinned Server with PostGIS and local attachment storage, seeds `mobile_offline_demo/Offline Field Sites` (layer `68910`) with attachments enabled, and creates the layer-scoped worker key. It uploads `ordinary-photo/server-readback.json` and the four Maestro reports in `published-android-results`.

To run just the photo scenario against an equivalently seeded fixture, put Python 3.11+, Java, `adb`, `curl`, `jq` and Maestro 2.12.1 on PATH. Install the Debug APK on a disposable emulator, clear its Field sandbox and start with airplane mode off. Use the same display size and density as the gate (822×1782 at 320 dpi). Keep the seeded server on host port `5189` or set `HONUA_E2E_HTTP_PORT` to match the fixture. The harness toggles airplane mode and uses `adb reverse` to reconnect to the fixture. Only the worker key goes to the emulator.

```bash
port=${HONUA_E2E_HTTP_PORT:-5189}
scripts/e2e/create-fixture-worker.sh "http://127.0.0.1:$port" /path/to/fixture-admin-key \
  "mobile_offline_demo/Offline Field Sites" /path/to/worker-key
python3 scripts/e2e/run-ordinary-photo-android.py emulator-5554 /tmp/field-photo-results /path/to/worker-key
```

The [live GeoServices test](../../../tests/Honua.Field.Core.Tests/Sync/FeatureSyncE2ETests.cs) covers a dropped upload acknowledgment. After the server stores the photo and the reply is lost, the test reopens the encrypted store, verifies the remote attachment and confirms that the original feature lists one attachment and Field made only one upload request.

## iPhone and iPad geometry

The [Apple workflow](../../../.github/workflows/field-apple.yml) builds Field for the iOS simulator and Mac Catalyst. Separate iPhone and iPad jobs check the app bundle's camera, microphone, photo-library and location permission descriptions before running Maestro. On iOS 27.2, both simulators displayed the empty Drafts, Outbox and Sent groups, captured a polygon, showed its GeoJSON and undid the last vertex. The [iPhone 18 Pro artifact](https://github.com/honua-io/honua-field/actions/runs/45895399423/artifacts/15154032425) and [iPad Pro 13-inch (M5) artifact](https://github.com/honua-io/honua-field/actions/runs/45895399423/artifacts/15153613595) contain the JUnit reports and screenshots.

## Published XLSForm iPhone gate

The iPhone workflow runs on a GitHub-hosted Apple silicon Mac (`runs-on: xcode-27`). It creates a fresh iOS 27.2 simulator with Xcode, installs the Field app, and starts Honua Server and PostgreSQL with PostGIS on the same Mac. The simulator reaches the server through a local fault-injection proxy. The archived job log identifies the runner provider and region. [Runner details](https://github.com/honua-io/honua-field/actions/runs/45900913043/job/140186163715#step:1:1)

Both mobile workflows take their Server commit from `scripts/e2e/fixtures/server-revision.txt`. Each hosted `server-readback.json` records the Field and Server commits and verifies that the Server checkout matches that pin.

Maestro downloads the form as a layer-scoped worker, saves a draft offline with two repeat rows and a photo, and resumes it after a cold start. The host publishes the next form version while the draft is offline. During the first sync, a proxy forwards the submission and replaces the successful response with HTTP 503. The outbox must survive another restart. After retry, the gate queries FeatureServer and the receipt table for one feature, one matching attachment and one accepted receipt at the original form version. The `published-ios-results` artifact includes per-stage JUnit results and the readback evidence. [iOS workflow](../../../.github/workflows/published-ios.yml)

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

### AWS Device Farm native smoke

Run the Records and geometry journey on a Device Farm phone or tablet. Use a fresh device session and an installable Android APK or iOS IPA built for the selected device architecture. The iPhone simulator IPA is available from the Field on Apple platforms workflow. Use Python 3.11 or newer and an AWS profile with Device Farm project, device, upload and remote session permissions in us-west-2.

```sh
python3 -m venv .venv-devicefarm
.venv-devicefarm/bin/pip install boto3==1.44.12 Appium-Python-Client==6.5.0 requests==2.34.2
.venv-devicefarm/bin/python scripts/e2e/run-devicefarm-smoke.py \
  --project-arn "$DEVICEFARM_PROJECT_ARN" --device-arn "$DEVICEFARM_DEVICE_ARN" \
  --app "$FIELD_APP" --revision "$FIELD_COMMIT" --output "$NEW_EVIDENCE_DIR"
```

The runner uploads that binary, waits for a device, verifies the three empty record groups, captures a point, checks the GeoJSON and undoes the edit. It records the binary digest, device, OS, session ARN, outcome and screenshots, then stops the session. Device Farm accounts meter device minutes. For workbook publication, disconnected capture, in-place upgrades and server-side receipt verification, run the published Android and iOS gates above.

### AWS physical Android in-place update

Use two APKs for `io.honua.collect` signed with the same certificate. The previous build must include **Sample Inspection Demo** and the candidate must have a higher Android version code. Use the following commands from the Field repository to build the test bundle. The device must be able to launch both APKs.

```sh
bundle=$(mktemp -d)
mkdir -p "$bundle/tests"
cp "$CANDIDATE_APK" "$bundle/candidate.apk"
cp scripts/e2e/run-installed-android-upgrade.py "$bundle/"
cp scripts/e2e/devicefarm/test_android_upgrade.py "$bundle/tests/"
echo 'pytest==8.4.2' > "$bundle/requirements.txt"
python3 -m pip download --only-binary=:all: -r "$bundle/requirements.txt" -d "$bundle/wheelhouse"
(cd "$bundle" && zip -qr "$bundle/field-upgrade.zip" candidate.apk tests run-installed-android-upgrade.py requirements.txt wheelhouse)
```

Schedule a Device Farm Android custom-environment run in `us-west-2`. Select the previous APK as the app, `field-upgrade.zip` as the Appium Python package, and `scripts/e2e/devicefarm/android-upgrade.yml` as the test spec. Select a physical device and set `executionConfiguration.skipAppResign` to `true` so Android can verify the update against the original signer.

The test saves one inspection on the previous build, updates it with `adb install -r`, and reopens Records on the same device. Download the customer artifacts for both screenshots and `upgrade.json`, which includes the installed APK digests, version codes, Android app UID, Outbox record ID and run ARN. The published-workbook tests above cover attachment bytes and server receipts.

On November 27, 2026 UTC, a physical Pixel 8 running Android 15 passed the update from build 4 (`6e3db0b`) to build 5 (`3662201`). Both APKs used the same verification key. Android retained the app UID and the original 32-character Outbox record ID. AWS Device Farm run `cf27473e-0c2c-4740-a731-b26bc602126d` contains the screenshots and installed APK digests.
