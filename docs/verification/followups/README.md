# Follow-up feature verification — Honua Collect (Android)

Four functional-gap features were added to close the distance to Survey123 /
Fulcrum parity, then wired into the MAUI app and run on the emulator (Google APIs
x86_64, API 35, KVM-accelerated). The build/run toolchain is the one documented
in [`../README.md`](../README.md).

| # | Feature | Where | Verified |
| --- | --- | --- | --- |
| 17 | **Offline persistence** — SQLite `IRecordStore` so Drafts/Outbox/Sent survive restarts | `Honua.Collect.Core/Storage/`, `CaptureStore` write-through, hydrate in `App` | **On device, end-to-end** (below) + 7 Core tests |
| 18 | **Attachment upload** — `addAttachment` multipart to the Feature Server | `GeoServicesFeatureSync.AddAttachmentAsync`, called from `MainPage` after submit | Wired; 6 Core transport tests (server round-trip needs a live feature) |
| 19 | **Form-package download** — server form package → `FormDefinition` | `Honua.Collect.Core/Forms/FormPackageClient`, "Download Latest Form" button | Button on device; 7 Core tests (round-trip needs a `FormServer` endpoint) |
| 20 | **Login / auth** — credential check → `AuthSession` | `Honua.Collect.Presentation/Auth/LoginViewModel`, "Account" tab | Page + validation on device (`01-login.png`) + 9 Presentation tests |

## Persistence — proven across an app restart

1. Captured a record (`site_name = Persist-Test-Alpha`, `status = In progress`)
   and submitted. The server was intentionally down, so it landed in the Outbox
   as **Failed — Connection failure** and was written through to SQLite.
2. The on-device database (`files/collect-records.db`) held the row verbatim:

   ```
   record_id   6966fed5c63e4309b3b9742572c123d0
   form_id     field-site
   lat/lon     21.31 / -157.81
   values_json {"site_name":"Persist-Test-Alpha","status":"in_progress"}
   sync_state  4 (Failed)      last_error  Connection failure
   ```

3. `adb shell am force-stop io.honua.collect` killed the process (clearing the
   in-memory list), then the app was relaunched cold.
4. The **Records** tab read **Drafts 0 · Outbox 1 · Sent 0** with the same record
   and its `Failed — Connection failure` state rehydrated from SQLite
   (`02-records-rehydrated.png`) — i.e. `SqliteRecordStore.LoadAllAsync`
   reconstructed the entry and its sync box on startup.

## Screenshots

| File | Demonstrates |
| --- | --- |
| `01-login.png` | The **Account** tab renders the `LoginViewModel`-bound sign-in page; tapping **Sign in** with empty fields shows live validation. |
| `02-records-rehydrated.png` | After force-stop + cold relaunch, the Outbox record is **restored from SQLite** with its failed sync state. |
| `03-home-download-form.png` | Home screen with the **Download Latest Form** action (form-package client) alongside Start New Inspection. |
