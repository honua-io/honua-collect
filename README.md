# Honua Collect

[![CI](https://github.com/honua-io/honua-collect/actions/workflows/ci.yml/badge.svg?branch=trunk)](https://github.com/honua-io/honua-collect/actions/workflows/ci.yml)
[![CodeQL](https://github.com/honua-io/honua-collect/actions/workflows/codeql.yml/badge.svg?branch=trunk)](https://github.com/honua-io/honua-collect/actions/workflows/codeql.yml)
[![OpenSSF Scorecard](https://api.securityscorecards.dev/projects/github.com/honua-io/honua-collect/badge)](https://scorecard.dev/viewer/?uri=github.com/honua-io/honua-collect)

**Honua Collect** is an offline-first mobile field data collection app — a
source-available alternative to Esri Survey123 / Field Maps and Fulcrum, built
on the [Honua](https://github.com/honua-io) geospatial platform. Dynamic smart
forms (conditional visibility, calculations, validation, repeatable sections),
encrypted offline storage (SQLCipher), connectivity-aware bidirectional sync
with field-level conflict review, GPS/geometry capture, photo/signature/barcode
capture, bulk export (CSV, GeoJSON, KML, Excel, GeoPackage, Shapefile), and
optional AI-assisted capture. Collect speaks the **Esri GeoServices protocol**
natively (FeatureServer `query`/`applyEdits`, `generateToken`), so it syncs to a
self-hosted [Honua server](https://github.com/honua-io/honua-server) — or an
existing ArcGIS FeatureServer — with **no Esri license and no vendor cloud** in
between.

## Status

🚧 **Early stage, active development — not yet a GA field app.** The honest
picture, kept current in [ROADMAP.md](ROADMAP.md), [BACKLOG.md](BACKLOG.md), and
[docs/COMPETITIVE-STRATEGY.md](docs/COMPETITIVE-STRATEGY.md):

- **Solid today:** the platform-neutral `Honua.Collect.Core` logic layer (form
  runtime, records, sync, conflicts, export, licensing, provenance, …) and the
  `Honua.Collect.Presentation` MVVM layer are implemented and unit-tested, with
  coverage gates and mutation testing in CI.
- **Verified on device (Android emulator):** login, form-package download,
  offline capture with live validation and repeats, SQLite persistence,
  GeoServices feature sync, and attachment upload — with captured evidence in
  [`docs/verification/`](docs/verification/).
- **Still in progress:** several device "last-mile" bindings (camera/GNSS/map
  polish, background sync, push, external GNSS) and Pro/Enterprise UX. Android
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

## Editions (open core)

A fully usable **Community** edition is free. Selected capabilities are gated to
**Pro** / **Enterprise** via a signed license key, verified offline on device
(enforcement protected by ELv2's anti-circumvention terms).

| Capability | Community | Pro | Enterprise |
| --- | :---: | :---: | :---: |
| Dynamic forms, validation, repeats, calculations | ✅ | ✅ | ✅ |
| Offline GeoPackage storage + sync | ✅ | ✅ | ✅ |
| GPS / geometry capture, offline basemaps | ✅ | ✅ | ✅ |
| Photo / signature capture | ✅ | ✅ | ✅ |
| **Reports & exports** (per-record PDF/Word, bulk export) | — | ✅ | ✅ |
| **AI-assisted capture** (voice/photo-to-fields, redaction) | — | ✅ | ✅ |
| **Advanced sync & GIS** (selective sync, external GNSS) | — | ✅ | ✅ |
| **Enterprise auth & admin** (SSO, roles, audit, MDM/white-label) | — | — | ✅ |

_Tier assignments are provisional and subject to change before GA._

## Documentation

Hosted platform docs live at [honua.gitbook.io/honuaio](https://honua.gitbook.io/honuaio/).
Repo docs:

| Doc | What it covers |
| --- | --- |
| [Self-hosting guide](docs/self-hosting.md) | Point Collect at a self-hosted Honua server (config surface, auth, the open-core boundary, quickstart). |
| [Esri compatibility matrix](docs/esri-compatibility.md) | What Collect supports vs Survey123 / Field Maps / Fulcrum, marked honestly with evidence links. |
| [Data residency & self-host posture](docs/data-residency-and-self-host.md) | Where records live and how the connection is secured. |
| [Migrating from Survey123 and Fulcrum](docs/migration-from-survey123-and-fulcrum.md) | Lowering the switching cost (XLSForm import, protocol reuse). |
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

## License (open core)

Honua Collect is **source-available under the [Elastic License 2.0](LICENSE)
(ELv2)**. In short: use it, modify it, redistribute it freely — but you may not
offer it to third parties as a hosted/managed service, and you may not
circumvent the license-key functionality that gates paid features.

It is built **on top of** the Honua SDK packages, which remain **Apache-2.0**
and are consumed as versioned NuGet packages — never vendored or relicensed:

| Layer | Repo | License | Role |
| --- | --- | --- | --- |
| `Honua.Sdk.*` | [honua-io/honua-sdk-dotnet](https://github.com/honua-io/honua-sdk-dotnet) | Apache-2.0 | platform-neutral SDK |
| `Honua.Mobile.*` | [honua-io/honua-mobile](https://github.com/honua-io/honua-mobile) | Apache-2.0 | mobile SDK foundation |
| **Honua Collect** | this repo | **ELv2** | the product |
