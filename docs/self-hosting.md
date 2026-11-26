# Self-hosting Honua Field

Honua Field is **open source under the [Apache License 2.0](../LICENSE)** and has no SaaS dependency: it talks to a server you choose by speaking
the **GeoServices REST** protocol (FeatureServer `query` / `applyEdits`,
`generateToken`). This guide shows how to point Field at a self-hosted Honua
server (or any GeoServices-compatible FeatureServer, including self-hosted
enterprise GIS servers that implement the required GeoServices REST endpoints)
**without a vendor-specific licence and without routing data through any
Honua-operated cloud**.

It is grounded in the app's real configuration surface — every key named here is
read by [`AppSettings.Load()`](../src/Honua.Field.App/Services/AppSettings.cs)
and wired in [`MauiProgram.cs`](../src/Honua.Field.App/MauiProgram.cs). For
where records live and how the connection is secured, see
[data-residency-and-self-host.md](./data-residency-and-self-host.md).

## What "self-hosted" means here

Field is the **client**. It captures and stores records on-device, then syncs
them to a FeatureServer **you operate**. There is no separate vendor data silo
and no phone-home — the server you configure is the system of record. The
companion Honua server (a separate component/repo) is what you stand up to be
that target; its deployment runbook is out of scope for this client repo (tracked
in [#37](https://github.com/honua-io/honua-field/issues/37)).

Any endpoint that answers the GeoServices FeatureServer contract works as the
target:

- a **self-hosted Honua server** in your own datacenter or VPC, or
- an existing **on-prem self-hosted enterprise GIS server** FeatureServer, or
- a local **PostGIS-backed** stack for development (see
  [`scripts/e2e/up.sh`](../scripts/e2e/up.sh)).

## Configuration surface

Connection details ship in configuration, never compiled into the binary. The
bundled asset is
[`src/Honua.Field.App/Resources/Raw/appsettings.json`](../src/Honua.Field.App/Resources/Raw/appsettings.json):

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

The three `server` values become the GeoServices endpoints Field calls
([`GeoServicesTarget`](../src/Honua.Field.Core/Sync/GeoServicesFeatureSync.cs)):

```
{baseUrl}/rest/services/{serviceId}/FeatureServer/{layerId}/query
{baseUrl}/rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits
{baseUrl}/rest/services/{serviceId}/FeatureServer/{layerId}/{objectId}/addAttachment
```

### Local-dev override (no rebuild)

For local development you can drop a gitignored `appsettings.local.json` into the
app data directory with a `demo.apiKey`; it is overlaid at startup by
[`AppSettings.ApplyLocalOverride`](../src/Honua.Field.App/Services/AppSettings.cs)
and a malformed file never breaks startup. This is a dev convenience only — see
authentication below for the real sign-in path.

## Authentication

Field authenticates against **your server's own token endpoint** — there is no
dependency on a vendor-specific or Honua account, so a self-host deployment uses its own
identity.

1. The user signs in on the Account tab. Credentials are POSTed to the server's
   GeoServices REST-style token endpoint
   ([`ServerCredentialVerifier`](../src/Honua.Field.Presentation/Auth/ServerCredentialVerifier.cs),
   default path `/sharing/rest/generateToken`), which returns a short-lived
   bearer token. **The password is never stored** — only the issued token and its
   server-supplied expiry are kept.
2. The token is presented as `Authorization: Bearer <token>` on every server
   request by
   [`AuthHeaderHandler`](../src/Honua.Field.Core/Enterprise/AuthHeaderHandler.cs).
3. The session is persisted to the platform secure store
   (Android Keystore / iOS Keychain) so it resumes across restarts, and is
   discarded once expired (fail-closed: a lapsed session is **not** silently
   downgraded to the anonymous fallback key).

> **Refresh tokens:** the session lifecycle in
> [`AuthSessionManager`](../src/Honua.Field.Core/Enterprise/AuthSessionManager.cs)
> supports proactive token refresh, but the current `generateToken` flow issues
> no refresh token, so renewal today means re-sign-in on expiry. First-class
> enterprise SSO (OIDC/SAML) is **planned**, tracked in
> [#7](https://github.com/honua-io/honua-field/issues/7).

## Optional certificate pinning

For a hardened or air-gapped deployment you can pin the server's TLS public key.
Add one or more Base64 SPKI hashes to `server.pinnedCertificates`; they are
applied by [`CertificatePinning`](../src/Honua.Field.Core/Sync/CertificatePinning.cs)
**in addition to** platform validation. Pinning is **opt-in** — with none
configured, standard platform TLS validation applies, so a self-hosted deployment
is never broken by a pin it didn't set.

Cleartext HTTP to a non-loopback host is rejected at startup by
[`EndpointSecurity`](../src/Honua.Field.Core/Sync/EndpointSecurity.cs); use
HTTPS for any real deployment (loopback `http://` is allowed only for local dev).

## Every feature is free

Every capability in the app is available with no license key, edition, or
activation step, so a self-hosted, air-gapped deployment has the full feature set.
Paid value lives on Honua Server, which is licensed server-side.

## Quickstart

1. **Stand up a FeatureServer target.** For local development, bring up the
   reference stack (Honua server + PostGIS with an editable layer):

   ```bash
   HONUA_SERVER_IMAGE=<your-honua-server-image> scripts/e2e/up.sh
   ```

   Set `HONUA_SERVER_REPO` if your `honua-server` checkout is not a sibling `../honua-server` directory.

   This serves a GeoServices FeatureServer the emulator reaches at
   `10.0.2.2:18080` (see [`docs/verification/e2e/`](./verification/e2e/)). For a
   real deployment, point at your own server / self-hosted enterprise GIS server instead.

2. **Configure Field.** Edit
   [`appsettings.json`](../src/Honua.Field.App/Resources/Raw/appsettings.json)
   with your `server.baseUrl`, `serviceId`, and `layerId`. Use HTTPS for any
   non-loopback host; add `pinnedCertificates` if you require pinning.

3. **Build and deploy** the app per the
   [README](../README.md#run-the-app-on-an-android-emulator).

4. **Sign in** on the Account tab against your server's token endpoint. Capture a
   record and sync it — it round-trips to your FeatureServer via
   [`GeoServicesFeatureSync`](../src/Honua.Field.Core/Sync/GeoServicesFeatureSync.cs),
   with no third-party licence and no data leaving your perimeter.

## See also

- [GeoServices compatibility matrix](./geoservices-compatibility.md) — what Field supports
  on GeoServices REST, marked honestly.
- [Data residency & self-host posture](./data-residency-and-self-host.md) — where
  records live and how the connection is secured.
- [Importing forms and field data](./migration-from-external-forms.md)
  — XLSForm import and protocol reuse.
