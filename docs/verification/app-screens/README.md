# App screens — verified on Android emulator

The MAUI app is now a tabbed Shell (Capture / Records / Geometry / Signature),
verified running on a KVM-accelerated Android-35 emulator.

| File | Demonstrates |
| --- | --- |
| `01-tabs.png` | Tabbed shell: Capture, Records, Geometry, Signature. |
| `02-records-outbox.png` | **Drafts/Outbox/Sent** (BACKLOG S4): a submitted record sits in the **Outbox** with `Failed — Connection failure` (server was down); header counts `Drafts 0 · Outbox 1 · Sent 0`. With a reachable server it moves to **Sent**. |
| `03-geometry-polygon.png` | **Geometry capture** (G1/G2): tap the canvas to drop vertices; a 4-vertex polygon with closed ring, `Polygon: 4 vertex(es) ✓`. Backed by the tested `MapCaptureViewModel`. |
| `04-signature.png` | **Signature/sketch pad** (C4/C5): freehand ink on a GraphicsView. |

`RecordBoxViewModel` (Drafts/Outbox/Sent grouping) is unit-tested in
`Honua.Collect.Presentation.Tests`. The photo widget is wired to MAUI
`MediaPicker` (camera/gallery); on-device photo capture needs a real
camera/gallery so it isn't exercised in the headless emulator run.

## Offline sync + conflict review (verified)

| File | Demonstrates |
| --- | --- |
| `05-sync-center.png` | **Offline sync center** (S3): a record captured while the server was *stopped* sat in the Outbox (failed); after restarting the server, "Sync now" uploaded it — `Outbox 0 · Sent 1 · Failed 0`, "Nothing pending". The row landed in Postgres (`objectid 9006`). Bound to `SyncCenterViewModel`. |
| `06-conflict-review.png` | **Manual conflict review** (S1): a field-by-field local-vs-server diff (Status: done/in_progress, Notes: fixed on site/awaiting parts) with per-field "Keep this" radios and "Keep all mine/server" — only the differing fields are shown (`priority`, identical, is omitted). Bound to `ConflictReviewViewModel`. |

The full offline→online flow was driven on the emulator: capture offline →
Outbox → restart server → **Sync now** → Sent (confirmed in the database).
