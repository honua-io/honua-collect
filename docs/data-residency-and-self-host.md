# Data residency & self-host posture

Where your field data lives and how it moves, for a regulated or air-gapped
deployment of Honua Collect. This documents the **client** posture as implemented
in this repository; the companion Honua server (a separate component) is what
Collect syncs to, and its deployment is out of scope here.

## Where records live

- **On the device, at rest:** captured records are stored in a local
  **SQLCipher-encrypted SQLite** database (`SqliteRecordStore`). The 256-bit key
  is generated on first run and held in the platform secure store — Android
  Keystore / iOS Keychain — never in the database, app config, or plaintext
  (`DbKeyProvider`). The encrypted DB can only be opened on that device by that
  app install.
- **On the server, when synced:** records sync to the FeatureServer you
  configure (`ServerBaseUrl` / `ServiceId` / `LayerId` in `AppSettings`) over the
  Esri **GeoServices** protocol (`GeoServicesFeatureSync`). That server is the
  system of record; Collect does not route data through any Honua-operated SaaS,
  and there is no separate vendor data silo.

## How the connection is secured

- **In transit:** HTTPS to the configured endpoint. Cleartext HTTP is rejected
  except for explicitly local-development addresses (`EndpointSecurity`).
- **Optional SPKI certificate pinning:** set `PinnedCertificateSpki` in
  `AppSettings` to pin the server's public-key hashes (`CertificatePinning`).
  Pinning is opt-in — with none configured, platform TLS validation applies, so a
  self-hosted deployment is never broken by a pin it didn't set.
- **Authentication:** credentials are exchanged for a short-lived bearer token at
  the server's `generateToken` endpoint; the token is presented on each request
  and (optionally) resumed from secure storage across restarts. The password is
  never stored.

## Identity provider

Collect authenticates against the configured server's token endpoint, so a
self-host deployment uses **its own** identity — there is no dependency on an
Esri or Honua account. First-class enterprise SSO (OIDC/SAML) is tracked in #7.

## Tenancy & entitlements

- The open-core edition boundary is enforced **offline** by a signed licence key
  verified on-device against an embedded public key (`Honua.Collect.Core.Licensing`);
  the matching private key is held by the licensing authority and is never in the
  app or this repository. No "phone home" is required to enforce entitlements.

## Air-gapped use

Collect is offline-first: capture, validation, visibility, calculated fields,
repeats, conflict detection, and entitlement enforcement all run with no network.
In an air-gapped environment, Collect syncs to a FeatureServer reachable on the
local network (the self-hosted Honua server or an on-prem ArcGIS Enterprise),
with optional certificate pinning to that host. No outbound internet connection is
required for the core capture-and-sync loop.

## What is not yet in this repository

Documented here for honesty: the **server-side** air-gapped deployment runbook
and a **round-trip interop test against a reference ArcGIS layer** are tracked in
#37 and live with the server component; they are not part of the Collect client.

## See also

- [Self-hosting guide](./self-hosting.md) — the configuration surface, auth, and
  open-core boundary for pointing Collect at a self-hosted server.
- [Esri compatibility matrix](./esri-compatibility.md) — what Collect supports vs
  the incumbents, marked honestly.
