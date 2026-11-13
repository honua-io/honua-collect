# Shell and map spike — issue #168

Experimental branch only; do not merge this spike into production. Release
2027.1 remains held for promotion. The candidate is MAUI Blazor Hybrid with
MapLibre GL JS. No shell decision has been made.

The shared Razor component runs in a separate MAUI application and a loopback
browser test host, over the existing FormSession and SqliteRecordStore. The
browser host uses a synthetic, unencrypted local fixture and interactive server
rendering; it does not demonstrate offline browser execution. Use synthetic
records only. No server endpoint or synchronization contract is invented here.

## Build and browser journey

On the authorized build host, run `bash scripts/shell-spike/validate.sh` through
`remote-run`, never a local .NET command on the lane host. Asset preparation is
`npm ci --ignore-scripts --prefix scripts/shell-spike` followed by
`npm run assets --prefix scripts/shell-spike`. Dependencies are pinned in the npm
lockfile, and the generated map distribution includes its license.

Run the browser host with `dotnet run --project
src/Honua.Collect.Spike.Web --no-launch-profile --urls http://127.0.0.1:5280` on
the build host. Set COLLECT_SPIKE_DATA to a fresh disposable directory. From
scripts/shell-spike run `npx playwright test inspection.spec.mjs` with
COLLECT_SPIKE_URL pointing to that loopback host. The journey checks required
field validation, local outbox creation and persistence after reload.

For Android, publish the separate Spike.Maui project for net10.0-android,
Release, with AndroidPackageFormats=apk, output under artifacts/shell-spike.
Install only the resulting io.honua.collect.spike package; this has a separate
application ID. No installable package has yet been verified. iOS requires the
existing macOS build/signing path and founder device provisioning; signing
secrets must never be committed.

## Founder journey and measurement sheet

Record the exact commit, package hash, build configuration, device model, OS,
archive hash and byte count, thermal state and screen reader for every run.
Run each latency measurement five times and retain raw observations. An
unmeasured row is pending, never a pass or a fallback decision.

| Gate | Pass mark | Current evidence |
|---|---|---|
| Android and iOS cold start | usable Today list under 3 s | Pending; measure process launch to usable list externally |
| Local assets and panning | 50,000 stored points, at least 30 rendered frames/s | Pending; current geometry is synthetic in memory; callback probe is diagnostic only |
| Offline archive | 2 GB archive, visible tiles under 300 ms, no network | Pending; archive integration not yet implemented |
| Photo to outbox | under 2 s | Pending; native capture not yet implemented |
| Accessibility | every field labelled and reachable | Markup labels present; device screen reader audit pending |
| Browser | automated inspection journey passes | Test authored; execution pending |
| iOS build | hosted macOS build green | Pending |

1. Install the exact measured package on the founder's Android and iOS phones.
2. Force-stop, launch, and measure until Today responds to an action.
3. Open Inspection. Submit with no asset name: expect a required-field message.
4. Enter an asset name and notes, queue, then force-stop and reopen: expect one
   additional pending inspection. This is local persistence, not server sync.
5. Open Map; verify points and use the ten-second diagnostic pan. Save its JSON,
   but obtain rendered-frame measurements using platform profiling tools.
6. Repeat navigation and the inspection with the platform screen reader.
7. Complete the pending archive/photo probes before selecting the shell. If a
   measured gate fails, record the failure and evaluate the approved native
   fallback; an incomplete implementation does not establish a technology fail.

This fixture intentionally does not persist photo metadata, run a new sync
engine, or alter the shipping app. Complete archive, capture, encrypted native
storage, stored-asset loading and startup instrumentation before founder gate.
