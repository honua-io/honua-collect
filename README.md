# Honua Collect

[![OpenSSF Scorecard](https://api.securityscorecards.dev/projects/github.com/honua-io/honua-collect/badge)](https://scorecard.dev/viewer/?uri=github.com/honua-io/honua-collect)

**Honua Collect** is an offline-first mobile field data collection product built
on the [Honua](https://github.com/honua-io) platform — a source-available
alternative to apps like Esri Survey123 and Fulcrum. Dynamic forms, GeoPackage
offline storage, connectivity-aware sync, GPS/geometry capture, media capture,
and 3D/AR scene support, with optional AI-assisted capture.

## Licensing (open core)

Honua Collect is **source-available under the [Elastic License 2.0](LICENSE)
(ELv2)**. In short: use it, modify it, redistribute it freely — but you may not
offer it to third parties as a hosted/managed service, and you may not
circumvent the license-key functionality that gates paid features.

It is built **on top of** the Honua SDK packages, which remain **Apache-2.0** and
are consumed as versioned NuGet packages — never vendored or relicensed:

| Layer | Repo | License | Role |
| --- | --- | --- | --- |
| `Honua.Sdk.*` | `honua-io/honua-sdk-dotnet` | Apache-2.0 | platform-neutral SDK |
| `Honua.Mobile.*` | `honua-io/honua-mobile` | Apache-2.0 | mobile SDK foundation |
| **Honua Collect** | this repo | **ELv2** | the product |

## Editions

A fully usable **Community** edition is free. Selected capabilities are gated to
**Pro** / **Enterprise** via a signed license key (enforced at runtime —
protected by ELv2's anti-circumvention terms).

| Capability | Community | Pro | Enterprise |
| --- | :---: | :---: | :---: |
| Dynamic forms, validation, repeats, calculations | ✅ | ✅ | ✅ |
| Offline GeoPackage storage + sync | ✅ | ✅ | ✅ |
| GPS / geometry capture, offline basemaps | ✅ | ✅ | ✅ |
| Photo / signature capture | ✅ | ✅ | ✅ |
| **Reports & exports** (per-record PDF/Word, bulk export) | — | ✅ | ✅ |
| **AI-assisted capture** (voice/photo-to-fields, redaction) | — | ✅ | ✅ |
| **Advanced sync & GIS** (conflict-review UI, selective sync, external GNSS) | — | ✅ | ✅ |
| **Enterprise auth & admin** (SSO, roles, audit, MDM/white-label) | — | — | ✅ |

_Tier assignments are provisional and subject to change before GA._

## Status

🚧 **Active development.** The `Honua.Collect.Core` logic layer, the
platform-neutral `Honua.Collect.Presentation` (MVVM) layer, and the
`Honua.Collect.App` (.NET MAUI) Android shell are in place, with offline capture,
SQLite persistence, GeoServices feature sync, attachment upload, form-package
download, and login wired and verified on an emulator (see
[`docs/verification/`](docs/verification/)). The entitlement/license-key layer and
several feature modules remain in progress.

## Build

Consuming the Honua packages requires read access to the private github-honua
feed:

```bash
gh auth refresh -s read:packages
export HONUA_GITHUB_PACKAGES_USER="$(gh api user --jq .login)"
export HONUA_GITHUB_PACKAGES_TOKEN="$(gh auth token)"
```

The logic and presentation layers build and test with the standard SDK:

```bash
dotnet test tests/Honua.Collect.Core.Tests/Honua.Collect.Core.Tests.csproj
dotnet test tests/Honua.Collect.Presentation.Tests/Honua.Collect.Presentation.Tests.csproj
```

## Running the app on an Android emulator

Building and deploying the MAUI app needs an Android SDK + JDK. The full
bootstrap (Temurin JDK 17 + Android SDK into `$HOME`, the
`-p:AndroidSdkDirectory` / `-p:JavaSdkDirectory` build flags, AVD creation, and
the install/launch commands) is documented in
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

The end-to-end server stack (Honua server + PostGIS with an editable feature
layer) is brought up by [`scripts/e2e/up.sh`](scripts/e2e/up.sh); the emulator
reaches the host at `10.0.2.2:18080`. See
[`docs/verification/e2e/`](docs/verification/e2e/) and
[`docs/verification/followups/`](docs/verification/followups/) for captured
evidence.
