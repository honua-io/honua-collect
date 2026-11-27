#!/usr/bin/env python3
"""Capture an offline photo on a disposable, freshly cleared Android emulator."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import runpy
import subprocess
import time
from urllib.parse import urlencode
from urllib.request import Request, urlopen
import uuid

helpers = runpy.run_path(str(Path(__file__).with_name("run-installed-published-android-upgrade.py")))
Android, PACKAGE = helpers["Android"], helpers["PACKAGE"]
check, run_flow, stop = helpers["check"], helpers["run_flow"], helpers["stop"]


def set_airplane_mode(device, enabled):
    device.shell("cmd", "connectivity", "airplane-mode", "enable" if enabled else "disable")
    # Wait for ConnectivityManager to observe the radio transition before Field uses it.
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        state = device.shell("dumpsys", "connectivity")
        active = re.search(r"^\s*Active default network:\s*(none|\d+)\s*$", state, re.I | re.M)
        if active and (active[1].lower() == "none") == enabled:
            return
        time.sleep(1)
    raise RuntimeError(f"Timed out waiting for the default network (airplane mode {enabled})")


def readback(layer_url, key, marker, photo=None):
    def get(path):
        request = Request(layer_url + path, headers={"X-API-Key": key})
        with urlopen(request, timeout=20) as response:
            return response.read()

    query = urlencode({"where": f"site_name='{marker}'", "outFields": "*", "f": "json"})
    features = json.loads(get("/query?" + query))["features"]
    if photo is None:
        check(not features, "The offline inspection already exists on the server")
        return
    check(len(features) == 1, f"Expected one feature, found {len(features)}")
    attributes = features[0]["attributes"]
    check(attributes["site_name"] == marker, "The returned feature has a different inspection name")
    check(attributes["status"] == "open" and attributes["priority"] == "normal",
          "The inspection did not preserve its coded status and default priority")
    check(attributes["inspection_date"] and int(attributes["sync_version"]) == 1
          and attributes["offline_action"] == "field-capture", "Missing field-site capture metadata")
    object_id = int(attributes["objectid"])
    path = f"/{object_id}/attachments"
    attachments = json.loads(get(path + "?f=json"))["attachmentInfos"]
    check(len(attachments) == 1, f"Expected one attachment, found {len(attachments)}")
    attachment = attachments[0]
    check(attachment["contentType"] == "image/jpeg", "Uploaded photo is not a JPEG")
    check(attachment["size"] == len(photo), "Attachment size differs from private capture")
    attachment_id = int(attachment["id"])
    downloaded = get(f"{path}/{attachment_id}")
    check(downloaded == photo, "Downloaded photo differs from the capture on the device")
    return {"featureId": object_id, "featureCount": len(features), "attachmentId": attachment_id,
            "attachmentCount": len(attachments), "bytes": len(photo),
            "sha256": hashlib.sha256(downloaded).hexdigest()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("serial")
    parser.add_argument("report", type=Path)
    parser.add_argument("key_file", type=Path)
    args = parser.parse_args()
    args.report.mkdir(parents=True, exist_ok=True)
    device = Android(os.environ.get("ADB", "adb"), args.serial)
    key = args.key_file.read_text().strip()
    check(bool(key), "The fixture key file is empty")
    check(device.shell("getprop", "ro.kernel.qemu") == "1", "Use a disposable emulator")
    port = int(os.environ.get("HONUA_E2E_HTTP_PORT", "5189"))
    endpoint = f"http://127.0.0.1:{port}"
    layer_url = f"{endpoint}/rest/services/mobile_offline_demo/FeatureServer/68910"
    marker = "fieldphoto" + uuid.uuid4().hex[:16]
    settings = {"server": {"baseUrl": endpoint}, "demo": {"apiKey": key}}
    check(device.shell("settings", "get", "global", "airplane_mode_on") == "0",
          "Start the emulator with airplane mode disabled")
    override = "files/appsettings.local.json"
    has_override = device.shell(f"run-as {PACKAGE} sh -c 'if test -e {override}; then echo yes; else echo no; fi'")
    check(has_override == "no",
          "Start with a freshly cleared app sandbox")
    args.phase = "offline capture"
    try:
        subprocess.run([*device.command, "shell", f"run-as {PACKAGE} sh -c 'mkdir -p files && cat > {override}'"],
                       input=json.dumps(settings), text=True, check=True, timeout=15)
        set_airplane_mode(device, True)
        run_flow(args, 1, "capture-ordinary-photo", INSPECTION_NAME=marker)
        stop(device)
        args.phase = "offline reopen"
        run_flow(args, 2, "verify-outbox")
        media = device.shell("run-as", PACKAGE, "find", "files/media", "-type", "f").splitlines()
        check(len(media) == 1, "Expected one private photo after process death")
        photo = device.adb("exec-out", "run-as", PACKAGE, "cat", media[0])
        check(photo.startswith(b"\xff\xd8"), "Private capture is not a JPEG")
        readback(layer_url, key, marker)
        args.phase = "online sync"
        device.adb("reverse", f"tcp:{port}", f"tcp:{port}")
        set_airplane_mode(device, False)
        run_flow(args, 3, "sync")
        first = readback(layer_url, key, marker, photo)
        stop(device)
        args.phase = "sent after restart"
        run_flow(args, 4, "verify-ordinary-photo-sent")
        after_restart = readback(layer_url, key, marker, photo)
        check(after_restart == first, "Feature or attachment identity changed after restart")
        (args.report / "server-readback.json").write_text(json.dumps(after_restart, indent=2) + "\n")
    except Exception:
        helpers["capture_failure"](args, device)
        raise
    finally:
        stop(device)
        device.shell("run-as", PACKAGE, "rm", "-f", override)
        device.shell("cmd", "connectivity", "airplane-mode", "disable")
        if f"tcp:{port} " in device.adb("reverse", "--list").decode():
            device.adb("reverse", "--remove", f"tcp:{port}")


if __name__ == "__main__":
    main()
