# Honua Collect

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

🚧 **Early scaffolding.** Repository foundation only — license, build pins, and
NuGet feed wiring. Application projects, the entitlement/license-key layer, and
feature modules are not yet implemented.

## Build

Consuming the Honua packages requires read access to the private github-honua
feed:

```bash
gh auth refresh -s read:packages
export HONUA_GITHUB_PACKAGES_USER="$(gh api user --jq .login)"
export HONUA_GITHUB_PACKAGES_TOKEN="$(gh auth token)"
```
