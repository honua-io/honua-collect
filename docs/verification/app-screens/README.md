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
