# Today crew walkthrough

This interactive prototype supports the partner research gate in [Honua Field #165](https://github.com/honua-io/honua-field/issues/165) and the [asset research protocol](https://github.com/honua-io/honua-console/issues/671). It contains two fictional stops, an inspection and a work request. Sync, asset history and the photo attachment are simulated. Notes and practice receipts stay in this browser until reset. Use synthetic data.

From the repository root run `python3 -m http.server 8000 --directory docs/verification/today`. Open the server in a browser on the phone or tablet. For a device on the same trusted network, use the host's LAN address. Use **Prototype controls** to set the simulated connection. Keep the page's web server reachable for reloads.

## Facilitated tasks

Recruit 3–5 partner staff as specified in #165. Give participants the following tasks individually. Let them choose their route through the screen. The facilitator operates the simulation controls when a scenario calls for a connection change.

1. Start offline. Find the highest-priority stop due this morning. Use the map to confirm the asset and check its history. Record an inspection note and attach the sample photo. Reload the page midway, finish the inspection, then queue it. Ask what is on this device and what is on the server.
2. Simulate **Accepted, receipt lost** and reconnect. Ask what the participant would do when the receipt is missing. Reconnect again. Ask them to show the receipt and confirm that the note, pinned form version and photo still belong to the same asset.
3. At the other stop, record an offline inspection and queue it. Simulate **Inspection task reassigned** and reconnect. Ask them to find their original evidence and describe the next action they expect. From the same asset create a separate offline work request with a title, notes, and the sample photo. Ask the participant to show its location on the asset and explain which information they expect to reach dispatch.
4. Set **Current candidate · title and asset** before they queue the request. Reload before queuing and ask the participant to recover the draft. After queuing, use **Accepted, receipt lost**, reload again, and reconnect. Ask the participant to show the receipt, find the original notes and sample photo, and explain what dispatch received. Verify the receipt stays the same on a third reconnect. Repeat with **Proposed request with evidence** in a fresh session only if time permits.

Observe at least one phone and one tablet. Try one-handed use, gloved touches, outdoor light, enlarged text and the device's screen reader. Record the device, conditions, task completion without prompting, wrong turns, missed touches, confusion about sync or ownership, and participant wording. Record findings without names or real asset identifiers. Use **Reset practice session** between participants.

Record observations against each task before deciding whether to implement the production Today UI. For the request scenario, measure unaided completion from asset selection to finding the receipt, repeated attempts to create another request while awaiting the receipt, and correct identification of which information reached dispatch. Set time and error targets with the research owner before sessions.

## Request contract exercised in this prototype

The current candidate submits a title plus the asset and asset profile IDs. Server checks write permission and uses the asset’s geometry. In this simulation, notes and the sample photo reference remain in browser storage before and after the receipt, with no upload path. The proposed scenario simulates delivery of the complete evidence set. A queued request keeps its original scenario across reload and retry. The production request flow depends on the Server contract, a released Mobile adapter, and the partner research gate. See [Mobile #458](https://github.com/honua-io/honua-mobile/pull/458) and [Server #7301](https://github.com/honua-io/honua-server/issues/7301).

Physical camera capture, authentication and real Server receipt verification have separate native integration gates.

## Chromium regression

Run from the repository root with Node.js and npm. Install Playwright and Chromium in a temporary directory to keep the prototype independent of browser tooling.

```sh
today_test_dir="$(mktemp -d)"
npm install --prefix "$today_test_dir" --no-save playwright@1.65.0
"$today_test_dir/node_modules/.bin/playwright" install chromium
NODE_PATH="$today_test_dir/node_modules" node docs/verification/today/request-regression.cjs
```

The regression runs both delivery scenarios at phone and tablet widths. It verifies draft and evidence persistence, a reload after a lost acknowledgement, receipt identity after reconnect and subsequent reload, and the absence of duplicate receipts. The runner serves the checked-in files to Chromium directly and uses simulated delivery.
