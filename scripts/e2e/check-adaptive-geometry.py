#!/usr/bin/env python3
"""Check adaptive geometry on an unlocked Android emulator with the candidate installed.

Run with the device serial and an artifact directory. The check uses screen
size/density overrides to exercise a phone and tablet, draws a polygon through
touch input, and compares the captured GeoJSON after resizing and rotation.
Use a dedicated emulator. Existing app data is kept, but the geometry page is
reopened to begin with an empty canvas. ADB can override the adb executable.
"""

import argparse
import json
import os
import re
import runpy
import struct
import subprocess
from pathlib import Path

helper = runpy.run_path(str(Path(__file__).with_name("run-installed-android-upgrade.py")))
Android, PACKAGE = helper["Android"], helper["PACKAGE"]
PHONE = (540, 1080)
TABLET = (1200, 1920)


def check(condition, message):
    if not condition:
        raise RuntimeError(message)


def node_bounds(root, *labels):
    for node in root.iter("node"):
        if node.get("package") != PACKAGE:
            continue
        resource = node.get("resource-id", "").rsplit("/", 1)[-1]
        if any(label in (node.get("text"), node.get("content-desc"), resource) for label in labels):
            bounds = Android.bounds(node)
            if len(bounds) == 4 and bounds[2] > bounds[0] and bounds[3] > bounds[1]:
                return bounds
    return None


def snapshot(device, directory, name):
    data = device.adb("exec-out", "screencap", "-p")
    check(data.startswith(b"\x89PNG\r\n\x1a\n"), "Expected PNG from Android")
    (directory / f"{name}.png").write_bytes(data)
    return struct.unpack_from(">II", data, 16)


def layout(device, directory, name, size, *, landscape, side_by_side, count):
    observed = {}
    expected_size = size[::-1] if landscape else size

    def read(root):
        observed.clear()
        observed["rotation"] = root.get("rotation")
        observed["map"] = node_bounds(root, "Geometry drawing area", "GeometryCanvas")
        observed["controls"] = {
            "geometryType": node_bounds(root, "GeometryType", "Polygon", "Point"),
            "zoomOut": node_bounds(root, "Zoom out", "−"),
            "zoomIn": node_bounds(root, "Zoom in", "+"),
            "offlineArea": node_bounds(root, "Offline area"),
            "undo": node_bounds(root, "Undo"),
            "clear": node_bounds(root, "Clear"),
            "done": node_bounds(root, "Done"),
        }
        status = f"Polygon: {count} vertex(es)" + (" ✓" if count >= 3 else "")
        observed["status"] = node_bounds(root, status)
        if (observed["rotation"] in (("1", "3") if landscape else ("0", "2"))
                and observed["map"] and all(observed["controls"].values())
                and (not count or observed["status"])):
            _, _, right, bottom = observed["map"]
            if all((left >= right if side_by_side else top >= bottom)
                   for left, top, _, _ in observed["controls"].values()):
                # WindowManager can report the new rotation before the display
                # compositor presents its first frame at the requested size.
                width, height = snapshot(device, directory, name)
                observed.update(width=width, height=height)
                if (width, height) == expected_size:
                    return dict(observed)
        return None

    try:
        result = device.screen(read, f"{name} map and controls")
    except RuntimeError as error:
        snapshot(device, directory, name + "-failed")
        raise RuntimeError(f"{error}. Observed {observed}") from error
    width, height = expected_size
    for left, top, right, bottom in (result["map"], *result["controls"].values()):
        check(0 <= left < right <= width and 0 <= top < bottom <= height,
              f"{name} has out-of-screen bounds {(left, top, right, bottom)}")
    return result


def captured_polygon(device):
    device.tap("Done")

    def read(root):
        for node in root.iter("node"):
            value = node.get("text", "")
            if value.startswith("{"):
                geometry = json.loads(value)
                if geometry.get("type") == "Polygon":
                    return geometry
        return None

    geometry = device.screen(read, "captured Polygon GeoJSON")
    rings = geometry["coordinates"]
    check(len(rings) == 1 and len(rings[0]) == 4 and rings[0][0] == rings[0][-1]
          and len({tuple(point) for point in rings[0]}) == 3,
          "Captured geometry did not contain three distinct vertices and a closing coordinate")
    device.tap("OK")
    return geometry


def read_override(device, command):
    return re.search(r"^Override \w+: (\S+)$", device.shell("wm", command), re.MULTILINE)


def check_home_width(device, directory, name, expected_width):
    observed = {}

    def read(root):
        button = node_bounds(root, "Start New Inspection")
        observed.update(rotation=root.get("rotation"), button=button)
        if root.get("rotation") == "0" and button and abs(button[2] - button[0] - expected_width) <= 12:
            return button
        return None

    try:
        left, top, right, bottom = device.screen(read, f"{name} centered home button", timeout=90)
    except RuntimeError as error:
        snapshot(device, directory, name + "-failed")
        raise RuntimeError(f"{error}. Observed {observed}") from error
    width, height = snapshot(device, directory, name)
    check(abs(left + right - width) <= 12 and 0 <= left < right <= width
          and 0 <= top < bottom <= height, f"{name} home button is off center or clipped")
    return {"width": width, "height": height, "button": (left, top, right, bottom)}


def run(device, directory, serial):
    size_override = read_override(device, "size")
    density_override = read_override(device, "density")
    rotation_settings = {key: device.shell("settings", "get", "system", key) for key in
                         ("accelerometer_rotation", "user_rotation")}
    report = {"serial": serial, "layouts": {}, "home": {}}
    try:
        device.shell("settings", "put", "system", "accelerometer_rotation", "0")
        device.shell("settings", "put", "system", "user_rotation", "0")
        device.shell("wm", "size", "540x1080")
        device.shell("wm", "density", "240")
        device.shell("am", "force-stop", PACKAGE)
        device.launch()
        # At 240 dpi, 1 dip = 1.5px. The home column caps at 720 dip and has
        # 24 dip padding on each side. Phone content fills the available width.
        report["home"]["phone"] = check_home_width(device, directory, "phone-home", 468)
        device.shell("wm", "size", "1200x1920")
        report["home"]["tablet"] = check_home_width(device, directory, "tablet-home", 1008)
        device.shell("wm", "size", "540x1080")
        check_home_width(device, directory, "phone-home-return", 468)
        device.tap("Geometry")
        phone = layout(device, directory, "phone-empty", PHONE, landscape=False, side_by_side=False, count=0)
        l, t, r, b = phone["controls"]["geometryType"]
        device.shell("input", "tap", str((l + r) // 2), str((t + b) // 2))
        device.tap("Polygon")
        device.element("Polygon: 0 vertex(es)")
        left, top, right, bottom = phone["map"]
        for count, (x, y) in enumerate(((0.3, 0.3), (0.7, 0.35), (0.5, 0.7)), start=1):
            device.shell("input", "tap", str(int(left + x * (right - left))),
                         str(int(top + y * (bottom - top))))
            device.element(f"Polygon: {count} vertex(es)" + (" ✓" if count == 3 else ""))
        report["layouts"]["phone-portrait"] = layout(
            device, directory, "phone-portrait", PHONE, landscape=False, side_by_side=False, count=3)
        expected = captured_polygon(device)
        report["polygon"] = expected

        for name, size, rotation, landscape, expanded in (
            ("phone-landscape", PHONE, "1", True, True),
            ("tablet-portrait", TABLET, "0", False, True),
            ("tablet-landscape", TABLET, "1", True, True),
            ("phone-portrait-return", PHONE, "0", False, False),
        ):
            device.shell("wm", "user-rotation", "lock", rotation)
            device.shell("wm", "size", f"{size[0]}x{size[1]}")
            report["layouts"][name] = layout(
                device, directory, name, size, landscape=landscape, side_by_side=expanded, count=3)
            check(captured_polygon(device) == expected, f"{name} changed the captured polygon")
        device.tap("Undo")
        report["layouts"]["phone-portrait-undo"] = layout(
            device, directory, "phone-portrait-undo", PHONE, landscape=False, side_by_side=False, count=2)
        report["passed"] = True
    except (RuntimeError, ValueError, subprocess.SubprocessError) as error:
        report["passed"] = False
        report["error"] = str(error)
        raise
    finally:
        failures = []
        commands = [("wm", key, override[1] if override else "reset")
                    for key, override in (("size", size_override), ("density", density_override))]
        for key in ("user_rotation", "accelerometer_rotation"):
            value = rotation_settings[key]
            commands.append(("settings", "delete", "system", key) if value == "null"
                            else ("settings", "put", "system", key, value))
        commands.append(("rm", "-f", device.xml_path))
        for command in commands:
            try:
                device.shell(*command)
            except subprocess.SubprocessError as error:
                failures.append(str(error))
        if failures:
            report["passed"] = False
            report["restorationErrors"] = failures
        (directory / "adaptive-geometry.json").write_text(json.dumps(report, indent=2) + "\n")
        check(not failures, "Could not restore Android settings. See adaptive-geometry.json")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("serial")
    parser.add_argument("artifacts", type=Path)
    args = parser.parse_args()
    args.artifacts.mkdir(parents=True, exist_ok=True)
    run(Android(os.environ.get("ADB", "adb"), args.serial), args.artifacts, args.serial)


if __name__ == "__main__":
    main()
