# Honua Collect

[![CI](https://github.com/honua-io/honua-collect/actions/workflows/ci.yml/badge.svg?branch=trunk)](https://github.com/honua-io/honua-collect/actions/workflows/ci.yml)
[![CodeQL](https://github.com/honua-io/honua-collect/actions/workflows/codeql.yml/badge.svg?branch=trunk)](https://github.com/honua-io/honua-collect/actions/workflows/codeql.yml)
[![OpenSSF Scorecard](https://api.securityscorecards.dev/projects/github.com/honua-io/honua-collect/badge)](https://scorecard.dev/viewer/?uri=github.com/honua-io/honua-collect)
[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)

**Honua Collect** is an open-source (Apache 2.0), offline-first mobile field
data collection app, built on the [Honua](https://github.com/honua-io) geospatial platform. Dynamic smart
forms (conditional visibility, calculations, validation, repeatable sections),
encrypted offline storage (SQLCipher), connectivity-aware bidirectional sync
with field-level conflict review, GPS/geometry capture, photo/signature/barcode
capture, bulk export (CSV, GeoJSON, KML, Excel, GeoPackage, Shapefile), and
optional AI-assisted capture. Collect speaks the **GeoServices REST protocol**
natively (FeatureServer `query`/`applyEdits`, `generateToken`), so it syncs to a
self-hosted [Honua server](https://github.com/honua-io/honua-server) — or an
existing GeoServices REST feature service — with **no third-party license and no vendor cloud** in
between.

## Status

🚧 **Early stage, active development — not yet a GA field app.** The honest
picture, kept current in [ROADMAP.md](ROADMAP.md), [BACKLOG.md](BACKLOG.md), and
[docs/COMPETITIVE-STRATEGY.md](docs/COMPETITIVE-STRATEGY.md):

- **Solid today:** the platform-neutral `Honua.Collect.Core` logic layer (form
  runtime, records, sync, conflicts, export, provenance, …) and the
  `Honua.Collect.Presentation` MVVM layer are implemented and unit-tested, with
  coverage gates and mutation testing in CI.
- **Verified on device (Android emulator):** login, form-package download,
  offline capture with live validation and repeats, SQLite persistence,
  GeoServices feature sync, and attachment upload — with captured evidence in
  [`docs/verification/`](docs/verification/).
- **Still in progress:** several device "last-mile" bindings (camera/GNSS/map
  polish, background sync, push, external GNSS) and enterprise auth/admin UX. Android
  is the verified target; the iOS/Windows/Mac Catalyst targets exist in the MAUI
  project but are unverified.

## Quick start

Prerequisites: .NET 10 SDK. The `Honua.Sdk.*` / `Honua.Mobile.*` packages come
from the github-honua NuGet feed, which requires an authenticated GitHub token
with `read:packages`:

```bash
gh auth refresh -s read:packages
export HONUA_GITHUB_PACKAGES_USER="$(gh api user --jq .login)"
export HONUA_GITHUB_PACKAGES_TOKEN="$(gh auth token)"
```

The logic and presentation layers build and test with the standard SDK — no
Android toolchain needed:

```bash
dotnet test tests/Honua.Collect.Core.Tests/Honua.Collect.Core.Tests.csproj
dotnet test tests/Honua.Collect.Presentation.Tests/Honua.Collect.Presentation.Tests.csproj
```

### Run the app on an Android emulator

Building and deploying the MAUI app needs an Android SDK + JDK. The full
bootstrap (Temurin JDK 17 + Android SDK into `$HOME`, AVD creation, and the
install/launch commands) is documented in
[`docs/verification/README.md`](docs/verification/README.md). On a
KVM-accelerated emulator (`/dev/kvm` must be accessible to your user):

```bash
export JAVA_HOME=$HOME/jdk17 ANDROID_HOME=$HOME/android-sdk
export PATH=$JAVA_HOME/bin:$ANDROID_HOME/platform-tools:$ANDROID_HOME/emulator:$PATH

emulator -avd honua -no-window -gpu swiftshader_indirect &
adb wait-for-device

# Debug deploy to the running emulator:
dotnet build src/Honua.Collect.App/Honua.Collect.App.csproj -f net10.0-android -t:Install \
  -p:AndroidSdkDirectory=$ANDROID_HOME -p:JavaSdkDirectory=$JAVA_HOME \
  -p:AdbTarget="-s emulator-5554"
adb shell am start -n io.honua.collect/crc6490b85cd101108deb.MainActivity
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

You are welcome to fork Collect and make it your own. Apache 2.0 permits
commercial use, modification and redistribution, including under your own brand,
as long as you keep the [LICENSE](LICENSE) and [NOTICE](NOTICE) files. The
"Honua" name and logo are trademarks, so a rebranded build should drop them.

### Rebrand

All branding lives in `src/Honua.Collect.App`:

| What | Where |
| --- | --- |
| App name (launcher label) | `<ApplicationTitle>` in `Honua.Collect.App.csproj` |
| In-app titles | `Title="Honua Collect"` in `AppShell.xaml`, and the title and heading in `MainPage.xaml` |
| Application / bundle id | `<ApplicationId>` (currently `io.honua.collect`) in `Honua.Collect.App.csproj`; set your own reverse-DNS id before you publish, because stores and devices treat it as the app's identity |
| Version | `<ApplicationDisplayVersion>` and `<ApplicationVersion>` in `Honua.Collect.App.csproj` |
| App icon | `Resources/AppIcon/appicon.svg` (background) and `appiconfg.svg` (foreground); background colour is the `Color` on the `<MauiIcon>` item in the csproj |
| Splash screen | `Resources/Splash/splash.svg` and the `Color` on the `<MauiSplashScreen>` item in the csproj |
| UI colours | `Resources/Styles/Colors.xaml` (`Primary`, `Secondary`, `Tertiary`, …; used by `Styles.xaml` for light and dark themes) and, on Android, `Platforms/Android/Resources/values/colors.xml` |
| Fonts | `Resources/Fonts/` plus the `ConfigureFonts` call in `MauiProgram.cs` |

### Server connection

The server is set in configuration, not code:
[`Resources/Raw/appsettings.json`](src/Honua.Collect.App/Resources/Raw/appsettings.json)
(`server.baseUrl`, `server.serviceId`, `server.layerId`, optional
`server.pinnedCertificates`). It is embedded at build time, so change it before you
build. Users sign in on the Account tab against that server's token endpoint. The
[self-hosting guide](docs/self-hosting.md) documents every key.

### Forms

- **From the server:** "Download Latest Form" on the Capture screen fetches a form
  package from the configured server's form-package API (served by Honua Server).
- **Bundled:** the offline fallback and demo forms are C# definitions in
  [`SampleForms.cs`](src/Honua.Collect.App/SampleForms.cs). Edit or replace them
  to ship your own built-in forms; field ids must match the target layer's
  attribute names so submissions apply cleanly.

### Basemap

The embedded map draws OpenStreetMap raster tiles. The tile URL template is
`OsmTileUrl.Template` in
[`src/Honua.Collect.Core/Maps/OsmTileUrl.cs`](src/Honua.Collect.Core/Maps/OsmTileUrl.cs)
(any XYZ `{z}/{x}/{y}` PNG source works). Point a distributed build at a tile
server you operate or license: the public OSM tile servers have a usage policy
that does not allow heavy or bulk use. The tile request User-Agent is set in
`MauiProgram.cs`.

### Build and distribute your own build

Follow [Quick start](#quick-start) for the toolchain and package feed, then build
a signed, self-contained Android package:

```bash
dotnet publish src/Honua.Collect.App/Honua.Collect.App.csproj -f net10.0-android -c Release \
  -p:AndroidPackageFormats=apk \
  -p:AndroidKeyStore=true \
  -p:AndroidSigningKeyStore=/path/to/your.keystore \
  -p:AndroidSigningKeyAlias=your-alias \
  -p:AndroidSigningKeyPass=env:ANDROID_KEY_PASS \
  -p:AndroidSigningStorePass=env:ANDROID_STORE_PASS
```

Sideload the resulting `*-Signed.apk` from `bin/Release/net10.0-android/publish/`
with `adb install -r <file>`, distribute it through your MDM, or build an `.aab`
(`-p:AndroidPackageFormats=aab`) for an app store. Without the signing properties
the build signs with the local debug key, which is fine for testing only. Android
is the verified target; the iOS, Mac Catalyst and Windows targets exist but are
unverified.

### Not yet configurable

These need a source change today; there is no setting for them yet:

- **Basemap source:** a compile-time constant (`OsmTileUrl.Template`), one source only.
- **Downloaded form id:** the Capture screen requests the form id `field_site`
  (`MainPage.xaml.cs`); the bundled forms are compiled in.
- **One target layer:** the app syncs to the single service and layer in
  `appsettings.json`.
- **Runtime or MDM configuration:** `ManagedAppConfig` exists in Core but is not
  wired into the app, so the server and branding cannot be changed after the build.
- **Tile request User-Agent:** hard-coded in `MauiProgram.cs`.

## Documentation

Hosted platform docs live at [honua.gitbook.io/honuaio](https://honua.gitbook.io/honuaio/).
Repo docs:

| Doc | What it covers |
| --- | --- |
| [Self-hosting guide](docs/self-hosting.md) | Point Collect at a self-hosted Honua server (config surface, auth, quickstart). |
| [GeoServices compatibility matrix](docs/geoservices-compatibility.md) | Supported GeoServices REST behavior, marked honestly with evidence links. |
| [Data residency & self-host posture](docs/data-residency-and-self-host.md) | Where records live and how the connection is secured. |
| [Migrating external forms](docs/migration-from-external-forms.md) | Lowering switching costs through XLSForm import and protocol reuse. |
| [Chain of custody & provenance](docs/chain-of-custody.md) | Ed25519-signed capture manifests: what they prove and current limits. |
| [Competitive strategy](docs/COMPETITIVE-STRATEGY.md) | The three-horizon positioning frame over [BACKLOG.md](BACKLOG.md). |
| [Mutation testing](docs/mutation-testing.md) | Stryker.NET setup and how the Core mutation score is measured. |

## Related Honua repos

| Repo | Role |
| --- | --- |
| [honua-server](https://github.com/honua-io/honua-server) | The multi-protocol geospatial server Collect syncs to (FeatureServer + form packages API). |
| [honua-mobile](https://github.com/honua-io/honua-mobile) | .NET MAUI mobile SDK foundation (transport, GeoPackage offline storage, location) — consumed as `Honua.Mobile.*` packages. |
| [honua-sdk-dotnet](https://github.com/honua-io/honua-sdk-dotnet) | Platform-neutral .NET SDK — consumed as `Honua.Sdk.*` packages. |
| [honua-console](https://github.com/honua-io/honua-console) | Web console for administering the server Collect talks to. |
| [honua-helm](https://github.com/honua-io/honua-helm) | Helm chart — the Kubernetes deploy path for a self-hosted server. |

## Security

Please report vulnerabilities privately to **security@honua.io** — see the
[org security policy](https://github.com/honua-io/.github/blob/main/SECURITY.md).
CI runs secret scanning over full git history (gitleaks), a locked-mode
vulnerable-dependency gate, CodeQL, Trivy, and OpenSSF Scorecard. On-device
records are encrypted at rest with SQLCipher.

## License

Honua Collect is licensed under the [Apache License, Version 2.0](LICENSE). You
can use, modify, fork and redistribute it, including commercially, provided you
keep the license and [NOTICE](NOTICE). Versions published before this change
remain under the license they were published with.

It is built **on top of** the Honua SDK packages, which are also Apache-2.0 and
are consumed as versioned NuGet packages, never vendored:

| Layer | Repo | License | Role |
| --- | --- | --- | --- |
| `Honua.Sdk.*` | [honua-io/honua-sdk-dotnet](https://github.com/honua-io/honua-sdk-dotnet) | Apache-2.0 | platform-neutral SDK |
| `Honua.Mobile.*` | [honua-io/honua-mobile](https://github.com/honua-io/honua-mobile) | Apache-2.0 | mobile SDK foundation |
| **Honua Collect** | this repo | Apache-2.0 | the field app |
