# Honua Field

[![CI](https://github.com/honua-io/honua-field/actions/workflows/ci.yml/badge.svg?branch=trunk)](https://github.com/honua-io/honua-field/actions/workflows/ci.yml)
[![CodeQL](https://github.com/honua-io/honua-field/actions/workflows/codeql.yml/badge.svg?branch=trunk)](https://github.com/honua-io/honua-field/actions/workflows/codeql.yml)
[![OpenSSF Scorecard](https://api.securityscorecards.dev/projects/github.com/honua-io/honua-field/badge)](https://scorecard.dev/viewer/?uri=github.com/honua-io/honua-field)
[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)

**Honua Field** is an open-source (Apache 2.0), offline-first mobile field
data collection app, built on the [Honua](https://github.com/honua-io) geospatial platform. Dynamic smart
forms (conditional visibility, calculations, validation, repeatable sections),
encrypted offline storage (SQLCipher), connectivity-aware bidirectional sync
with field-level conflict review, GPS/geometry capture, photo/signature/barcode
capture, bulk export (CSV, GeoJSON, KML, Excel, GeoPackage, Shapefile), and
optional AI-assisted capture. Field speaks the **GeoServices REST protocol**
natively (FeatureServer `query`/`applyEdits`, `generateToken`), so it syncs to a
self-hosted [Honua server](https://github.com/honua-io/honua-server) — or an
existing GeoServices REST feature service — with **no third-party license and no vendor cloud** in
between.

From the geometry screen, choose **Add GPS vertex** to average three fresh
location fixes with reported accuracy of 25 m or better. Use it for a point
or for each vertex of a line or polygon.

## Status

🚧 **Early stage, active development — not yet a GA field app.** The honest
picture, kept current in [ROADMAP.md](ROADMAP.md) and [BACKLOG.md](BACKLOG.md):

- **Solid today:** the platform-neutral `Honua.Field.Core` logic layer (form
  runtime, records, sync, conflicts, export, provenance, …) and the
  `Honua.Field.Presentation` MVVM layer are implemented and unit-tested, with
  coverage gates and mutation testing in CI.
- **Verified on device (Android emulator):** login, form-package download,
  offline capture with live validation and repeats, SQLite persistence,
  GeoServices feature sync, and attachment upload — with captured evidence in
  [`docs/verification/`](docs/verification/).
- Field places editing controls below the map in narrow windows and beside it
  in wide windows, including a phone turned sideways.
  Rotating or resizing the window preserves the captured vertices. Sign-in and
  inspection forms use a centered, width-limited column on larger displays.
  The Records screen keeps Drafts, Outbox and Sent in a single scrolling list.
- On iPhone and iPad simulators, Field can draw a polygon, show its GeoJSON
  and undo the last vertex. The Apple workflow also builds Mac Catalyst.
  [Phone and tablet verification](docs/verification/e2e/README.md#iphone-and-ipad-geometry)
- Two AWS Device Farm iPhone simulator runs preserved an offline XLSForm draft
  or queued submission across an app upgrade. Honua Server verified the
  original form version, repeat rows and photo after sync.
  [iOS upgrade evidence](docs/verification/e2e/README.md#installed-ios-upgrade-on-aws)
- Remaining device work includes physical-camera capture, external GNSS receiver qualification, map editing,
  background sync, push notifications and enterprise sign-in. Windows device
  verification is also pending.

## Quick start

Install the .NET 11 SDK pinned in [global.json](global.json). Published Honua
packages use GitHub Packages. This branch also pins SDK and Mobile validation
packages to specific GitHub Actions artifacts. Authenticate `gh` with upstream
Actions read access and GitHub Packages read access.

From the repository root, prepare a temporary NuGet configuration and restore
the two test projects. The verifier checks each archive and package against
the [recorded checksums](.github/actions/mobile-validation-feed/artifacts.json).

```bash
export HONUA_GITHUB_PACKAGES_USER="$(gh api user --jq .login)"
export HONUA_GITHUB_PACKAGES_TOKEN="$(gh auth token)"
feed_dir=$(mktemp -d)
validator="$PWD/.github/actions/mobile-validation-feed/restore.py"
cp NuGet.config "$feed_dir/NuGet.config"
(cd "$feed_dir" && RUNNER_TEMP="$feed_dir" python3 "$validator")
dotnet nuget update source github-honua --configfile "$feed_dir/NuGet.config" \
  --username '%HONUA_GITHUB_PACKAGES_USER%' --password '%HONUA_GITHUB_PACKAGES_TOKEN%' \
  --store-password-in-clear-text
for project in Core Presentation; do
  test_project="tests/Honua.Field.$project.Tests/Honua.Field.$project.Tests.csproj"
  dotnet restore "$test_project" --configfile "$feed_dir/NuGet.config"
  dotnet test "$test_project" --no-restore
done
```

The temporary configuration stores the environment variable names. Keep those
variables set for subsequent builds. These tests run with the standard .NET SDK.
See the [offline runtime guide](docs/mobile-offline-adoption.md#assigned-work-pilot)
for the pilot build switch and release package requirements.

### Run the app on an Android emulator

Building and deploying the MAUI app needs Android SDK tooling and Temurin
JDK 21. Field runs on Android API 24 or newer. On a KVM-accelerated
emulator (`/dev/kvm` must be accessible to your user):

```bash
export JAVA_HOME=$HOME/jdk21 ANDROID_HOME=$HOME/android-sdk
export PATH=$JAVA_HOME/bin:$ANDROID_HOME/platform-tools:$ANDROID_HOME/emulator:$PATH

dotnet workload install maui-android
dotnet build src/Honua.Field.App/Honua.Field.App.csproj -f net11.0-android \
  -t:InstallAndroidDependencies -p:AcceptAndroidSDKLicenses=True -p:RestoreConfigFile="$feed_dir/NuGet.config" \
  -p:AndroidSdkDirectory=$ANDROID_HOME -p:JavaSdkDirectory=$JAVA_HOME

emulator -avd honua -no-window -gpu swiftshader_indirect &
adb wait-for-device

# Debug deploy to the running emulator:
dotnet build src/Honua.Field.App/Honua.Field.App.csproj -f net11.0-android -t:Install -p:RestoreConfigFile="$feed_dir/NuGet.config" \
  -p:AndroidSdkDirectory=$ANDROID_HOME -p:JavaSdkDirectory=$JAVA_HOME \
  -p:AdbTarget="-s emulator-5554"
adb shell monkey -p io.honua.collect -c android.intent.category.LAUNCHER 1
```

### Local end-to-end server

[`scripts/e2e/up.sh`](scripts/e2e/up.sh) brings up an isolated Honua server +
PostGIS with an editable feature layer; the emulator reaches the host at
`10.0.2.2:18080`. See [`docs/verification/e2e/`](docs/verification/e2e/) and
[`docs/verification/followups/`](docs/verification/followups/) for captured
evidence.

## Every feature is free

Every capability in the app is available to everyone: there are no in-app
editions, license keys, or feature gates. Paid value lives on
[Honua Server](https://github.com/honua-io/honua-server), which is licensed
server-side, and in support.

## Fork and customize

You are welcome to fork Field and make it your own. Apache 2.0 permits
commercial use, modification and redistribution, including under your own brand,
as long as you keep the [LICENSE](LICENSE) and [NOTICE](NOTICE) files. The
"Honua" name and logo are trademarks, so a rebranded build should drop them.

### Rebrand

All branding lives in `src/Honua.Field.App`:

| What | Where |
| --- | --- |
| App name (launcher label) | `<ApplicationTitle>` in `Honua.Field.App.csproj` |
| In-app titles | `Title="Honua Field"` in `AppShell.xaml`, and the title and heading in `MainPage.xaml` |
| Application / bundle id | `<ApplicationId>` (currently `io.honua.collect`) in `Honua.Field.App.csproj`; keep this ID for upgrades of Honua Collect. Give independently branded forks their own reverse-DNS ID |
| Version | `<ApplicationDisplayVersion>` and `<ApplicationVersion>` in `Honua.Field.App.csproj` |
| App icon | `Resources/AppIcon/appicon.svg` (background) and `appiconfg.svg` (foreground); background colour is the `Color` on the `<MauiIcon>` item in the csproj |
| Splash screen | `Resources/Splash/splash.svg` and the `Color` on the `<MauiSplashScreen>` item in the csproj |
| UI colours | `Resources/Styles/Colors.xaml` (`Primary`, `Secondary`, `Tertiary`, …; used by `Styles.xaml` for light and dark themes) and, on Android, `Platforms/Android/Resources/values/colors.xml` |
| Fonts | `Resources/Fonts/` plus the `ConfigureFonts` call in `MauiProgram.cs` |

### Upgrade from Honua Collect

The launcher displays Honua Field. Install it over Honua Collect using the
same distribution signing identity to retain existing app data and credentials.
Run `python3 scripts/check-upgrade-identity.py` to check the source identity, or
pass `--apk Field.apk --previous-apk Collect.apk` with the Android build tools
on PATH to also compare APK identities, version codes and signing certificates.

To exercise an installed upgrade on a fresh Android emulator, supply signed
Debug APKs for Collect 1.0 and Field from the same signing key:

```bash
python3 scripts/e2e/run-installed-android-upgrade.py emulator-5554 Collect.apk Field.apk
```

The harness creates a queued inspection and a separate sketch in Collect,
upgrades in place, opens Field, and checks the app UID, record ID and sketch
hash. The [offline adoption journey](docs/mobile-offline-adoption.md) covers
the broader migration, including drafts, assignments and sync history.

### Server connection

The server is set in configuration, not code:
[`Resources/Raw/appsettings.json`](src/Honua.Field.App/Resources/Raw/appsettings.json)
(`server.baseUrl`, `server.formId`, `server.serviceId`, `server.layerId`, optional
`server.pinnedCertificates`). It is embedded at build time, so change it before you
build. Users sign in on the Account tab against that server's token endpoint. The
[self-hosting guide](docs/self-hosting.md) documents every key.

### Forms

For a published XLSForm, set `server.formId` to its Honua Server form ID. The
default is `field_site`. Sign in, then tap **Download Latest Form** on the Capture
screen and check the version shown. Tap **Start New Inspection** to collect a
record. Enable offline use when publishing the form so workers can open it after
disconnecting or restarting Field. **Save draft** keeps the inspection in
**Records** with its original form version. **Submit** queues it for upload.

**Sample Inspection Demo** opens the bundled feature-layer form. To try offline
photo collection, enter a site name, choose a status, tap **Add photo**, then
**Submit**. You can close Field while the inspection is in the outbox. Once
connected, open **Sync** and tap **Sync now**. If Honua Server accepts the
feature first, the inspection stays in **Outbox** with the status **Feature
sent. Photos still need sync.** Tap **Sync now** again on the original layer.
If an upload reply was lost, Field checks the attachment name and SHA-256 on
that feature. The inspection moves to **Sent** once every photo is confirmed.
[Sample form](src/Honua.Field.App/SampleForms.cs) · [Photo sync verification](docs/verification/e2e/README.md#ordinary-feature-photos-on-android)

### Basemap

The embedded map uses the XYZ raster URL in
[`OsmTileUrl.Template`](src/Honua.Field.Core/Maps/OsmTileUrl.cs).
With the public OpenStreetMap source, **Check offline area** checks tiles already
cached at the current zoom. The public service's policy prohibits offline area
prefetch. For **Download area**, configure a source you operate or license for
offline use. Set its XYZ URL in `OsmTileUrl.Template` and set the adjacent
`AllowOfflinePrefetch` to `true` before building. Alternate URL templates
use separate caches. The app reports ready only when every planned tile is readable
from disk. [OpenStreetMap tile usage policy](https://operations.osmfoundation.org/policies/tiles/)

### Build and distribute your own build

Follow [Quick start](#quick-start) for the toolchain and package feed, then build
a signed, self-contained Android package:

```bash
dotnet publish src/Honua.Field.App/Honua.Field.App.csproj -f net11.0-android -c Release \
  -p:AndroidPackageFormats=apk \
  -p:AndroidKeyStore=true \
  -p:AndroidSigningKeyStore=/path/to/your.keystore \
  -p:AndroidSigningKeyAlias=your-alias \
  -p:AndroidSigningKeyPass=env:ANDROID_KEY_PASS \
  -p:AndroidSigningStorePass=env:ANDROID_STORE_PASS
```

Sideload the resulting `*-Signed.apk` from `bin/Release/net11.0-android/publish/`
with `adb install -r <file>`, distribute it through your MDM, or build an `.aab`
(`-p:AndroidPackageFormats=aab`) for an app store. Without the signing properties
the build signs with the local debug key, which is fine for testing only.
Platform verification is summarized under [Status](#status).

### Not yet configurable

These need a source change today; there is no setting for them yet:

- **Basemap source:** a compile-time constant (`OsmTileUrl.Template`), one source only.
- **Bundled inspection target:** the sample inspection uses the single service
  and layer in `appsettings.json`. Published inspections use the layer bound to the form on Honua Server.
- **Runtime or MDM configuration:** `ManagedAppConfig` exists in Core but is not
  wired into the app, so the server and branding cannot be changed after the build.
- **Tile request User-Agent:** hard-coded in `MauiProgram.cs`.

## Documentation

Hosted platform docs live at [honua.gitbook.io/honuaio](https://honua.gitbook.io/honuaio/).
Repo docs:

| Doc | What it covers |
| --- | --- |
| [Self-hosting guide](docs/self-hosting.md) | Point Field at a self-hosted Honua server (config surface, auth, quickstart). |
| [GeoServices compatibility matrix](docs/geoservices-compatibility.md) | Supported GeoServices REST behavior, marked honestly with evidence links. |
| [Shared offline runtime adoption](docs/mobile-offline-adoption.md) | Migration requirements, weekly Android and iOS gates, and the founder phone upgrade journey. |
| [Data residency & self-host posture](docs/data-residency-and-self-host.md) | Where records live and how the connection is secured. |
| [Migrating external forms](docs/migration-from-external-forms.md) | XLSForm import and protocol reuse for existing forms and data. |
| [Chain of custody & provenance](docs/chain-of-custody.md) | Ed25519-signed capture manifests: what they prove and current limits. |
| [Device and server verification](docs/verification/e2e/README.md) | Reproduce the Android, iOS and live Honua Server checks and inspect their artifacts. |
| [Mutation testing](docs/mutation-testing.md) | Stryker.NET setup and how the Core mutation score is measured. |

## Related Honua repos

| Repo | Role |
| --- | --- |
| [honua-server](https://github.com/honua-io/honua-server) | The multi-protocol geospatial server Field syncs to (FeatureServer + form packages API). |
| [honua-mobile](https://github.com/honua-io/honua-mobile) | .NET MAUI mobile SDK foundation (transport, GeoPackage offline storage, location) — consumed as `Honua.Mobile.*` packages. |
| [honua-sdk-dotnet](https://github.com/honua-io/honua-sdk-dotnet) | Platform-neutral .NET SDK — consumed as `Honua.Sdk.*` packages. |
| [honua-console](https://github.com/honua-io/honua-console) | Web console for administering the server Field talks to. |
| [honua-helm](https://github.com/honua-io/honua-helm) | Helm chart — the Kubernetes deploy path for a self-hosted server. |

## Security

Please report vulnerabilities privately to **security@honua.io** — see the
[org security policy](https://github.com/honua-io/.github/blob/main/SECURITY.md).
CI runs secret scanning over full git history (gitleaks), a locked-mode
vulnerable-dependency gate, CodeQL, Trivy, and OpenSSF Scorecard. On-device
records are encrypted at rest with SQLCipher.

## License

Honua Field is licensed under the [Apache License, Version 2.0](LICENSE). You
can use, modify, fork and redistribute it, including commercially, provided you
keep the license and [NOTICE](NOTICE). Versions published before this change
remain under the license they were published with.

It is built **on top of** the Honua SDK packages, which are also Apache-2.0 and
are consumed as versioned NuGet packages, never vendored:

| Layer | Repo | License | Role |
| --- | --- | --- | --- |
| `Honua.Sdk.*` | [honua-io/honua-sdk-dotnet](https://github.com/honua-io/honua-sdk-dotnet) | Apache-2.0 | platform-neutral SDK |
| `Honua.Mobile.*` | [honua-io/honua-mobile](https://github.com/honua-io/honua-mobile) | Apache-2.0 | mobile SDK foundation |
| **Honua Field** | this repo | Apache-2.0 | the field app |
