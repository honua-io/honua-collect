# Self-hosting Honua Collect

Honua Collect is **source-available under the [Elastic License 2.0](../LICENSE)
(ELv2)** and has no SaaS dependency: it talks to a server you choose by speaking
the **Esri GeoServices** protocol (FeatureServer `query` / `applyEdits`,
`generateToken`). This guide shows how to point Collect at a self-hosted Honua
server (or any GeoServices-compatible FeatureServer, including on-prem ArcGIS
Enterprise) **without an Esri licence and without routing data through any
Honua-operated cloud**.

It is grounded in the app's real configuration surface — every key named here is
read by [`AppSettings.Load()`](../src/Honua.Collect.App/Services/AppSettings.cs)
and wired in [`MauiProgram.cs`](../src/Honua.Collect.App/MauiProgram.cs). For
where records live and how the connection is secured, see
[data-residency-and-self-host.md](./data-residency-and-self-host.md).

## What "self-hosted" means here

Collect is the **client**. It captures and stores records on-device, then syncs
them to a FeatureServer **you operate**. There is no separate vendor data silo
and no phone-home — the server you configure is the system of record. The
companion Honua server (a separate component/repo) is what you stand up to be
that target; its deployment runbook is out of scope for this client repo (tracked
in [#37](https://github.com/honua-io/honua-collect/issues/37)).

Any endpoint that answers the GeoServices FeatureServer contract works as the
target:

- a **self-hosted Honua server** in your own datacenter or VPC, or
- an existing **on-prem ArcGIS Enterprise** FeatureServer, or
- a local **PostGIS-backed** stack for development (see
  [`scripts/e2e/up.sh`](../scripts/e2e/up.sh)).

## Configuration surface

Connection details ship in configuration, never compiled into the binary. The
bundled asset is
[`src/Honua.Collect.App/Resources/Raw/appsettings.json`](../src/Honua.Collect.App/Resources/Raw/appsettings.json):

```jsonc
{
  "server": {
    "baseUrl": "https://collect.example.gov",   // your server's base URL
    "serviceId": "field_inspections",            // feature service id
    "layerId": 0,                                 // layer id within the service
    "pinnedCertificates": []                      // optional Base64 SPKI pins
  },
  "demo": {
    "apiKey": null                               // dev-only fallback; null in production
  }
}
```

| Key | Type | Required | Maps to | Purpose |
| --- | --- | :---: | --- | --- |
| `server.baseUrl` | string | ✅ | `AppSettings.ServerBaseUrl` | Base URL of your FeatureServer host. Used as the HTTP client base address and for the GeoServices target. |
| `server.serviceId` | string | ✅ | `AppSettings.ServiceId` | The feature service id of the editable layer. |
| `server.layerId` | int | ✅ | `AppSettings.LayerId` | The layer id within that service. |
| `server.pinnedCertificates` | string[] | — | `AppSettings.PinnedCertificateSpki` | Optional Base64 SPKI public-key pins (see [Certificate pinning](#optional-certificate-pinning)). Empty/absent = standard platform TLS. |
| `demo.apiKey` | string\|null | — | `AppSettings.DemoApiKey` | Development-only fallback credential used **only when no user has signed in**. `null` in production — no credential ships in the binary. |

The three `server` values become the GeoServices endpoints Collect calls
([`GeoServicesTarget`](../src/Honua.Collect.Core/Sync/GeoServicesFeatureSync.cs)):

```
{baseUrl}/rest/services/{serviceId}/FeatureServer/{layerId}/query
{baseUrl}/rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits
{baseUrl}/rest/services/{serviceId}/FeatureServer/{layerId}/{objectId}/addAttachment
```

### Local-dev override (no rebuild)

For local development you can drop a gitignored `appsettings.local.json` into the
app data directory with a `demo.apiKey`; it is overlaid at startup by
[`AppSettings.ApplyLocalOverride`](../src/Honua.Collect.App/Services/AppSettings.cs)
and a malformed file never breaks startup. This is a dev convenience only — see
authentication below for the real sign-in path.

## Authentication

Collect authenticates against **your server's own token endpoint** — there is no
dependency on an Esri or Honua account, so a self-host deployment uses its own
identity.

1. The user signs in on the Account tab. Credentials are POSTed to the server's
   ArcGIS-style token endpoint
   ([`ServerCredentialVerifier`](../src/Honua.Collect.Presentation/Auth/ServerCredentialVerifier.cs),
   default path `/sharing/rest/generateToken`), which returns a short-lived
   bearer token. **The password is never stored** — only the issued token and its
   server-supplied expiry are kept.
2. The token is presented as `Authorization: Bearer <token>` on every server
   request by
   [`AuthHeaderHandler`](../src/Honua.Collect.Core/Enterprise/AuthHeaderHandler.cs).
3. The session is persisted to the platform secure store
   (Android Keystore / iOS Keychain) so it resumes across restarts, and is
   discarded once expired (fail-closed: a lapsed session is **not** silently
   downgraded to the anonymous fallback key).

> **Refresh tokens:** the session lifecycle in
> [`AuthSessionManager`](../src/Honua.Collect.Core/Enterprise/AuthSessionManager.cs)
> supports proactive token refresh, but the current `generateToken` flow issues
> no refresh token, so renewal today means re-sign-in on expiry. First-class
> enterprise SSO (OIDC/SAML) is **planned**, tracked in
> [#7](https://github.com/honua-io/honua-collect/issues/7).

## Optional certificate pinning

For a hardened or air-gapped deployment you can pin the server's TLS public key.
Add one or more Base64 SPKI hashes to `server.pinnedCertificates`; they are
applied by [`CertificatePinning`](../src/Honua.Collect.Core/Sync/CertificatePinning.cs)
**in addition to** platform validation. Pinning is **opt-in** — with none
configured, standard platform TLS validation applies, so a self-hosted deployment
is never broken by a pin it didn't set.

Cleartext HTTP to a non-loopback host is rejected at startup by
[`EndpointSecurity`](../src/Honua.Collect.Core/Sync/EndpointSecurity.cs); use
HTTPS for any real deployment (loopback `http://` is allowed only for local dev).

## The open-core boundary (ELv2)

A fully usable **Community** edition is free. Selected capabilities are gated to
**Pro** / **Enterprise** by a signed licence key. The boundary is the single
source of truth in code
([`CollectFeatures.MinimumEdition`](../src/Honua.Collect.Core/Editions/CollectFeatures.cs)),
and the README table mirrors it:

| Capability | Community | Pro | Enterprise |
| --- | :---: | :---: | :---: |
| Forms (validation, repeats, calculations, cascades) | ✅ | ✅ | ✅ |
| Offline store + GeoServices sync | ✅ | ✅ | ✅ |
| GPS / geometry capture | ✅ | ✅ | ✅ |
| Photo / signature capture | ✅ | ✅ | ✅ |
| Reports & exports (`ReportsAndExports`) | — | ✅ | ✅ |
| AI-assisted capture (`AiAssistedCapture`) | — | ✅ | ✅ |
| Advanced sync & GIS — selective sync, external GNSS (`AdvancedSyncAndGis`) | — | ✅ | ✅ |
| Enterprise auth & admin — SSO, roles, audit, MDM (`EnterpriseAuthAndAdmin`) | — | — | ✅ |

How entitlement is enforced for a self-host:

- The edition boundary is verified **offline, on-device** by
  [`Honua.Collect.Core.Licensing`](../src/Honua.Collect.Core/Licensing/LicenseService.cs):
  a signed licence key is checked against an embedded authority public key. **No
  "phone home" is required** — entitlements work fully air-gapped.
- With no key activated, the app runs the **Community baseline** (a genuinely
  useful, fully self-hostable field-collection tool). A valid Pro/Enterprise key
  (held in secure storage and activated at startup by
  [`MauiProgram.ActivateStoredLicenseAsync`](../src/Honua.Collect.App/MauiProgram.cs))
  unlocks the gated features above.
- The matching private signing key is held by the licensing authority and is
  **never** in the app or this repository.

> Tier assignments are provisional and subject to change before GA. Licensing
> integration is tracked in [#9](https://github.com/honua-io/honua-collect/issues/9).

## Quickstart

1. **Stand up a FeatureServer target.** For local development, bring up the
   reference stack (Honua server + PostGIS with an editable layer):

   ```bash
   scripts/e2e/up.sh
   ```

   This serves a GeoServices FeatureServer the emulator reaches at
   `10.0.2.2:18080` (see [`docs/verification/e2e/`](./verification/e2e/)). For a
   real deployment, point at your own server / ArcGIS Enterprise instead.

2. **Configure Collect.** Edit
   [`appsettings.json`](../src/Honua.Collect.App/Resources/Raw/appsettings.json)
   with your `server.baseUrl`, `serviceId`, and `layerId`. Use HTTPS for any
   non-loopback host; add `pinnedCertificates` if you require pinning.

3. **Build and deploy** the app per the
   [README](../README.md#running-the-app-on-an-android-emulator).

4. **Sign in** on the Account tab against your server's token endpoint. Capture a
   record and sync it — it round-trips to your FeatureServer via
   [`GeoServicesFeatureSync`](../src/Honua.Collect.Core/Sync/GeoServicesFeatureSync.cs),
   with no Esri licence and no data leaving your perimeter.

5. **(Optional) Activate a licence key** to unlock Pro/Enterprise features. The
   app runs the Community baseline until one is applied.

## See also

- [Esri compatibility matrix](./esri-compatibility.md) — what Collect supports
  vs Survey123 / Fulcrum / ArcGIS, marked honestly.
- [Data residency & self-host posture](./data-residency-and-self-host.md) — where
  records live and how the connection is secured.
- [Migrating from Survey123 and Fulcrum](./migration-from-survey123-and-fulcrum.md)
  — lowering the switching cost.
- [Competitive strategy](./COMPETITIVE-STRATEGY.md) — the positioning frame.
</content>
</invoke>
