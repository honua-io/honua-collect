# Honua Collect — architecture & roadmap

## Repository strategy (decided)

Honua Collect is a **single product repo**. We are not splitting the field-
collection product across `honua-mobile` and `honua-collect`.

- **`Honua.Sdk.*`** (`honua-io/honua-sdk-dotnet`) — platform-neutral SDK. Stays
  **Apache-2.0**, consumed here as versioned NuGet packages. This is the genuine
  shared foundation and is *not* absorbed.
- **`honua-mobile`** — slims toward **lower-level mobile primitives** (transport,
  GeoPackage storage, device location, native bindings). Collect consumes these
  as packages (`Honua.Mobile.Sdk`, `Honua.Mobile.Offline`, `Honua.Mobile.Maui`).
- **Mobile field-collection product layer** (today `Honua.Mobile.Field` /
  `Honua.Mobile.FieldCollection`) — **consolidated into this repo (Apache-2.0)**. Third-
  party Apache portions retain their notices.

## Migration plan: port the field layer into Collect

1. **Scaffold (this commit)** — repo foundation, `Honua.Collect.Core`, editions
   tier model, tests, MAUI app shell.
2. **Port `Honua.Mobile.Field` adapters** → `Honua.Collect.Core` (forms runtime,
   capture workflow, validation glue, media attachment model), consuming the
   SDK's field contracts directly instead of the mobile package.
3. **Port `Honua.Mobile.FieldCollection.Core`** app services (assignments,
   exports, drafts) → Collect.
4. **Slim `honua-mobile`** — drop the migrated surface; keep primitives only.
5. **Build the Community baseline UX** in `Honua.Collect.App` (forms, camera,
   geometry capture, offline sync status).

## Editions

`Honua.Collect.Core/Editions` encodes the tier matrix. Free Community baseline;
paid features gated to Pro/Enterprise. Runtime enforcement is a signed license-key
verification.

| Capability | Min edition |
| --- | --- |
| Reports & exports | Pro |
| AI-assisted capture | Pro |
| Advanced sync & GIS | Pro |
| Enterprise auth & admin | Enterprise |
