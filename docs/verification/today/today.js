const stops = [
  { id: "HY-104", name: "Oak Street hydrant", location: "18 Oak Street", priority: "High priority", due: "09:30", history: "Oct 12 · Cap seal replaced", x: "18%", y: "24%" },
  { id: "GV-218", name: "Park Road gate valve", location: "72 Park Road", priority: "Routine", due: "13:00", history: "Sep 8 · Valve operated freely", x: "52%", y: "63%" }
];
const modes = { inspect: "Asset inspection v7", request: "Asset work request v2" };
const storageKey = "honua-field-today-practice-v1";
const element = (id) => document.getElementById(id);
const emptySession = () => ({ selected: null, mode: "inspect", online: false, records: {}, receipts: {} });
let session = JSON.parse(localStorage.getItem(storageKey)) || emptySession();
const save = () => localStorage.setItem(storageKey, JSON.stringify(session));
const recordKey = () => session.selected + "/" + session.mode;
const statusText = {
  draft: "Draft · saved on this device",
  queued: "Queued · saved on this device",
  lost: "Awaiting receipt · saved on this device",
  accepted: "Synced",
  reassigned: "Reassigned · evidence kept for review"
};
function currentRecord() {
  return session.records[recordKey()] ||= { notes: "", photo: "", status: "draft" };
}
function drawDetail() {
  const stop = stops.find((item) => item.id === session.selected);
  element("detail").hidden = !stop;
  if (!stop) return;
  const record = currentRecord();
  const editable = record.status === "draft";
  element("asset-title").textContent = stop.name;
  element("asset").textContent = stop.id + " · " + stop.location;
  element("history").textContent = "Last visit: " + stop.history;
  for (const mode of Object.keys(modes)) {
    element(mode).setAttribute("aria-pressed", String(mode === session.mode));
  }
  element("form-title").textContent = modes[session.mode];
  element("notes").value = record.notes;
  element("notes").readOnly = !editable;
  element("attachment").textContent = record.photo ? "Attached: " + record.photo : "No photo attached";
  element("photo").hidden = !editable || Boolean(record.photo);
  element("queue").hidden = !editable;
  element("queue").disabled = !editable || !(record.notes.trim() || record.photo);
  element("record-status").textContent = statusText[record.status] +
    (record.status === "accepted" ? " · receipt " + session.receipts[record.command] : "");
}
function draw() {
  element("network").textContent = session.online ? "Connected (simulated)" : "Working offline";
  for (const button of document.querySelectorAll("[data-stop]")) {
    button.setAttribute("aria-current", String(button.dataset.stop === session.selected));
  }
  drawDetail();
}
function selectStop(stop) {
  session.selected = stop.id;
  session.mode = "inspect";
  draw();
  save();
  element("asset-title").focus();
}
for (const [index, stop] of stops.entries()) {
  const button = document.createElement("button");
  button.className = "stop";
  button.dataset.stop = stop.id;
  const title = document.createElement("strong");
  title.textContent = (index + 1) + ". " + stop.name;
  const description = document.createElement("span");
  description.textContent = stop.priority + " · Due " + stop.due + " · " + stop.id;
  button.append(title, description);
  button.onclick = () => selectStop(stop);
  element("list").append(button);
  const pin = document.createElement("button");
  pin.className = "pin";
  pin.dataset.stop = stop.id;
  pin.textContent = String(index + 1);
  pin.setAttribute("aria-label", "Stop " + (index + 1) + ", " + stop.name);
  pin.style.left = stop.x;
  pin.style.top = stop.y;
  pin.onclick = () => selectStop(stop);
  element("map").append(pin);
}
for (const view of ["list", "map"]) {
  element(view + "-view").onclick = () => {
    for (const name of ["list", "map"]) {
      element(name).hidden = name !== view;
      element(name + "-view").setAttribute("aria-pressed", String(name === view));
    }
  };
}
for (const mode of Object.keys(modes)) {
  element(mode).onclick = () => {
    session.mode = mode;
    drawDetail();
    save();
  };
}
element("notes").oninput = () => {
  const record = currentRecord();
  record.notes = element("notes").value;
  element("queue").disabled = !(record.notes.trim() || record.photo);
  save();
};
element("photo").onclick = () => {
  currentRecord().photo = session.selected + "-" + session.mode + "-sample.jpg";
  save();
  drawDetail();
};
element("capture").onsubmit = (event) => {
  event.preventDefault();
  const record = currentRecord();
  if (record.status !== "draft" || !(record.notes.trim() || record.photo)) return;
  record.command = Array.from(crypto.getRandomValues(new Uint8Array(16)),
    (byte) => byte.toString(16).padStart(2, "0")).join("");
  record.status = "queued";
  save();
  drawDetail();
  element("announcement").textContent = "Queued " + modes[session.mode] + " for " + session.selected + ".";
};
element("retry").onclick = () => {
  session.online = true;
  const outcome = element("response").value;
  let count = 0;
  for (const [key, record] of Object.entries(session.records)) {
    if (record.status !== "queued" && record.status !== "lost") continue;
    count++;
    if (session.receipts[record.command]) {
      record.status = "accepted";
    } else if (outcome === "reassigned" && key.endsWith("/inspect")) {
      record.status = "reassigned";
    } else {
      session.receipts[record.command] = "R-" + record.command.slice(0, 8).toUpperCase();
      record.status = outcome === "lost" ? "lost" : "accepted";
    }
  }
  save();
  draw();
  element("announcement").textContent = count ? "Sync attempted for " + count + (count === 1 ? " queued record." : " queued records.") : "No records waiting to sync.";
};
element("offline").onclick = () => {
  session.online = false;
  save();
  draw();
};
element("reset").onclick = () => {
  session = emptySession();
  localStorage.removeItem(storageKey);
  element("list-view").click();
  element("response").value = "accepted";
  element("announcement").textContent = "Practice session reset.";
  draw();
};
draw();
