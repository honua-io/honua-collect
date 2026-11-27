# Today crew walkthrough

This interactive prototype supports the partner research gate in [Honua Field #165](https://github.com/honua-io/honua-field/issues/165) and the [asset research protocol](https://github.com/honua-io/honua-console/issues/671). It contains two fictional stops, an inspection and a work request. Sync, asset history and the photo attachment are simulated. Notes and practice receipts stay in this browser until reset. Use synthetic data.

From the repository root run `python3 -m http.server 8000 --directory docs/verification/today`. Open the server in a browser on the phone or tablet. For a device on the same trusted network, use the host's LAN address. Use **Prototype controls** to set the simulated connection. Keep the page's web server reachable for reloads.

## Facilitated tasks

Recruit 3–5 partner staff as specified in #165. Give participants the following tasks individually. Let them choose their route through the screen. The facilitator operates the simulation controls when a scenario calls for a connection change.

1. Start offline. Find the highest-priority stop due this morning. Use the map to confirm the asset and check its history. Record an inspection note and attach the sample photo. Reload the page midway, finish the inspection, then queue it. Ask what is on this device and what is on the server.
2. Simulate **Accepted, receipt lost** and reconnect. Ask what the participant would do when the receipt is missing. Reconnect again. Ask them to show the receipt and confirm that the note, pinned form version and photo still belong to the same asset.
3. At the other stop, record an offline inspection and queue it. Simulate **Inspection task reassigned** and reconnect. Ask them to find their original evidence and describe the next action they expect. From the same asset create a separate offline work request with notes and the sample photo. Reconnect with **Accepted** and verify the linked asset and receipt.

Observe at least one phone and one tablet. Try one-handed use, gloved touches, outdoor light, enlarged text and the device's screen reader. Record the device, conditions, task completion without prompting, wrong turns, missed touches, confusion about sync or ownership, and participant wording. Record findings without names or real asset identifiers. Use **Reset practice session** between participants.

Record observations against the three tasks before deciding whether to implement the production Today UI. Physical camera capture, authentication and real Server receipt verification have separate native integration gates.
