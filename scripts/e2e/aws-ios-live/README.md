# Installed iOS upgrade against Honua Server on Device Farm

Use an authorized AWS Device Farm project with the custom macOS Tahoe iPhone simulator lane in us-west-2. The host and the simulator share loopback. The test starts its own PostgreSQL and Server there, and removes the preinstalled copy of Field from the disposable simulator before installing the historical build. It stops local services through the existing published-iOS gate and detaches the PostgreSQL disk image on exit.

Download `aws-ios-simulator-apps` from [the pinned build](https://github.com/honua-io/honua-field/actions/runs/45980545095). The workflow builds baseline `6e3db0b0c48c63d1bdb6fd1d7f00c2030dc33c46` and candidate `c6551c28995ed4f15cd9db92ec5df95938ae0da4`, then records each archive SHA-256. Supply the checksummed self-contained macOS arm64 Server archive built from `ea127fc0916f2bce70596deb7e4f11784ce9f7d1`. Its top-level directory must be `published-server/` and contain the executable `Honua.Server`. The archive and the seed must come from the same revision.

```bash
scripts/e2e/aws-ios-live/stage.sh /tmp/field-aws-live /path/to/app-artifact \
  /path/to/honua-server /path/to/server.tar.gz /path/to/server.sha256
cd /tmp/field-aws-live
HONUA_DEVICEFARM_PROJECT_FILE=/private/path/to/project-arn python3 schedule.py
```

Use Python 3.11 or later to schedule runs. The uploader prints the run ARN and stores it in `runarn`. To reuse a validated app upload when only the test harness changes, set `HONUA_DEVICEFARM_APP_RECEIPT` to the previous staging directory’s `app-upload.json`. The uploader checks project ownership, upload status, and the staged IPA digest first. Device Farm's artifact list contains the existing gate report under `published-ios`. Check `input-digests.txt` and the gate's result, final feature readback, and attachment hash. The gate forms a real outbox record in the old build, updates the installed app in place, publishes a newer workbook version, and sends the queued record under its original version. Passing requires the expected server-side record and its attachment bytes exactly once. The worker key and database credential are generated for the disposable fixture.

To build another pair, dispatch `aws-ios-artifacts.yml` with `previous_revision` and `candidate_revision` once the workflow is registered on the repository's default branch. Both inputs must be full, distinct Git commits with compatible Apple toolchains. Set `HONUA_E2E_IOS_PREVIOUS_REVISION`, `HONUA_E2E_FIELD_REVISION`, and `HONUA_E2E_SERVER_REVISION` for staging to select the matching app archives and server seed. Set `HONUA_E2E_UPGRADE_STATE=draft` to exercise an upgrade before enqueueing. The default `outbox` covers upgrade after enqueueing. Staging puts the resulting selections in the checksummed `provenance.txt`, which the AWS run records along with the input digests. The manifest also records the staging harness commit and the exact iOS picker harness commit. Override the latter with `HONUA_E2E_IOS_PICKER_REVISION` when qualifying another compatible app pair. Set `HONUA_DEVICEFARM_DEVICE_ARN` for another approved iOS simulator in us-west-2.

## Physical iPhone qualification

Build the same two commits with the `ios-arm64` workflow input and download
`aws-ios-device-apps`. Verify both IPA checksums. The workflow checks the
Mach-O device platform, the shared `io.honua.collect` bundle ID and the
increasing build numbers before publishing. The pinned baseline is build 4
and the candidate is build 5. This packaging job leaves signing to the device
lab and requires no change to Field's distribution identity.

Use an available physical iPhone in the `Honua-Field` Device Farm project in
`us-west-2`. Record the model, OS and session ARN. The physical iPhone needs
network access to a disposable HTTPS Honua Server fixture at the pinned
revision. Keep administrative workbook publication and evidence readback on
the fixture operator's private connection. Public Device Farm devices need
a credential-scoped route to the worker API. Device Farm's
[VPC integration for private devices](https://docs.aws.amazon.com/devicefarm/latest/developerguide/vpc-eni.html)
requires a private device and configured VPC access.

The historical apps embed an Android emulator endpoint, and their iOS debug
override accepts only development loopback addresses. Dispatch
`AWS iOS artifacts` with `runtime=ios-arm64` and the fixture's HTTPS origin
as `fixture_url` to package the two pinned source revisions with the
fixture service, layer and form IDs. The origin must contain no credentials.
Seed `field_inspections` layer `68950` and publish version 1 of
`field_runtime_inspection` using `scripts/e2e/publish-field-runtime-fixture.sh`.
Enable the fixture's OIDC portal credential verifier with
`Oidc__Enabled=true`, `Authentication__PortalCredentialVerifier__UseOidc=true`
and `Authentication__PortalCredentialVerifier__FallBackToAdminVerifier=false`.
Configure the isolated issuer, audience and worker role. Restrict the worker
route and role to the form, layer and attachment endpoints. Keep credential
request bodies out of ingress logs.

Obtain a short-lived worker JWT through the fixture's identity provider for
each scenario. Enter the worker username and paste the JWT in Field's Account
secret field. In these pinned builds the secret field supplies the existing
OIDC JWT to `POST /sharing/rest/generateToken` for exchange. Keep JWTs out of
build inputs, screenshots and evidence. Revoke the worker access and tear down
the fixture after the sessions. Record the build run and artifact digests.
Use the native picker with a known JPEG on the physical device and record the
digest of the bytes Field stored before syncing.

To check device installation before setting up the workbook fixture, run
`run-devicefarm-smoke.py` with the baseline `--app` and `--revision`,
the candidate `--upgrade-app` and `--upgrade-revision`, and the physical
device and project ARNs. The script uploads both IPAs before reserving a
device. It checks the installed build numbers and confirms both builds
launch in the same session, then stops the session. The result JSON records
the session, artifact digests and both observed build numbers. This check
covers deployment. The workbook scenarios also require the data checks below.

For each of `draft` and `outbox`, start a fresh remote access session with
the baseline IPA and connect Appium to its `remoteDriverEndpoint`. Install
the candidate through
[`InstallToRemoteAccessSession`](https://docs.aws.amazon.com/devicefarm/latest/APIReference/API_InstallToRemoteAccessSession.html)
in the same physical session, without uninstalling, resetting, signing in
again or recreating the record. Check that the installed build changes from
4 to 5 and that the candidate can read the old encrypted draft or outbox.
Device Farm normally re-signs uploaded iOS apps. Both installations must
preserve access to the original Field Keychain data for the upgrade to count.

Keep independently captured evidence from both scenarios. For `draft`,
open the preserved draft and enqueue it after the upgrade. For `outbox`,
send the existing queued record. In each case verify version 2 is available
while the record syncs under the original form and version 1. Read the
record back from Honua Server, count the accepted record and attachment
exactly once, compare both repeat rows and verify the downloaded attachment
against the captured digest. Stop each session after evidence collection.

The simulator results above cover both scenarios. Physical qualification
requires both physical sessions to pass the same readback checks.

The [November 27, 2026 Device Farm preflight](https://us-west-2.console.aws.amazon.com/devicefarm/home?region=us-west-2#/mobile/projects/afe2be56-2a71-433e-8d74-1b0354523ff2/sessions/e662226e-e746-428e-883c-b2258c6d0a4b)
established an XCUITest connection on an Apple iPhone 18 Pro running iOS 27.0.
Appium listed installed apps and confirmed the device was clean of Field.
The session ended before Field installation or fixture access. The project had
no private device or VPC path to the seeded fixture.
