#!/usr/bin/env python3
"""Verify one averaged GPS point through the installed app on a disposable Android emulator.

Install the candidate APK on a dedicated unlocked emulator first. The script
grants location permission, injects mock GPS fixes through the emulator console,
and verifies the point returned by Geometry > Done. No server is required.
"""

import argparse
import json
import os
from pathlib import Path
import runpy
import threading

helper = runpy.run_path(str(Path(__file__).with_name("run-installed-android-upgrade.py")))
Android, PACKAGE = helper["Android"], helper["PACKAGE"]
LONGITUDE, LATITUDE = -122.4194, 37.7749


def read_geometry(root):
    for node in root.iter("node"):
        value = node.get("text", "")
        if value.startswith("{"):
            data = json.loads(value)
            if data.get("type") == "Point":
                return data
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("serial")
    parser.add_argument("artifacts", type=Path)
    args = parser.parse_args()
    args.artifacts.mkdir(parents=True, exist_ok=True)
    device = Android(os.environ.get("ADB", "adb"), args.serial)

    def inject():
        output = device.adb("emu", "geo", "fix", str(LONGITUDE), str(LATITUDE))
        if b"OK" not in output:
            raise RuntimeError("Emulator did not accept a GPS fix")

    # Check the console before changing permissions or interacting with the app.
    inject()
    for permission in ("ACCESS_COARSE_LOCATION", "ACCESS_FINE_LOCATION"):
        device.shell("pm", "grant", PACKAGE, f"android.permission.{permission}")

    device.launch()
    device.tap("Geometry")
    device.tap("Add GPS vertex")

    stop = threading.Event()
    errors = []

    def send_fixes():
        try:
            while not stop.is_set():
                inject()
                stop.wait(1.25)
        except Exception as error:
            errors.append(error)

    sender = threading.Thread(target=send_fixes)
    sender.start()
    try:
        device.screen(
            lambda root: next((True for node in root.iter("node")
                              if node.get("text", "").startswith("GPS vertex added.")), None),
            "the averaged GPS vertex status",
        )
        (args.artifacts / "gps-point.png").write_bytes(device.adb("exec-out", "screencap", "-p"))
        device.tap("Done")
        point = device.screen(read_geometry, "captured Point GeoJSON")
    finally:
        stop.set()
        sender.join()
        if errors:
            raise errors[0]

    expected = (LONGITUDE, LATITUDE)
    actual = point["coordinates"]
    if len(actual) != 2 or any(abs(a - b) > 0.0001 for a, b in zip(actual, expected)):
        raise RuntimeError(f"Expected GPS point near {expected}, got {actual}")
    (args.artifacts / "gps-point.json").write_text(json.dumps(point, indent=2) + "\n")
    device.tap("OK")


if __name__ == "__main__":
    main()
