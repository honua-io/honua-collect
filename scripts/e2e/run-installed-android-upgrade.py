#!/usr/bin/env python3
"""Check the in-place Collect 1.0 → Field 1.1 Android upgrade.

Supply locally signed debuggable APKs that use the same signing key, and a
fresh, booted Android emulator with an unlocked screen. The script requires an
Android SDK adb and Python 3. It installs both builds, disables the emulator's
network, and leaves the resulting installation and records available for
inspection. It refuses a target where the app is already installed.

The legacy bundled inspection creates an offline Outbox record. The legacy
Capture Widgets demo writes the sketch separately. Legacy Save draft does
not persist a draft, so this check covers the queue and private sketch file.
"""

import argparse
import hashlib
import re
import subprocess
import time
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path

PACKAGE = "io.honua.collect"
OUTBOX_RECORD = re.compile(r"[0-9a-f]{32}")


class Android:
    def __init__(self, adb, serial):
        self.command = [adb, "-s", serial]
        self.xml_path = f"/data/local/tmp/honua-upgrade-{uuid.uuid4().hex}.xml"

    def adb(self, *args):
        return subprocess.check_output(
            [*self.command, *args], stderr=subprocess.PIPE, timeout=120
        )

    def shell(self, *args):
        return self.adb("shell", *args).decode().strip()

    def install(self, apk, *, update=False):
        flags = ["-r"] if update else []
        if "Success" not in self.adb("install", "--no-streaming", *flags, str(apk)).decode():
            raise RuntimeError("Android did not report successful installation")
        self.shell("run-as", PACKAGE, "id")

    def launch(self):
        output = self.shell(
            "cmd", "package", "resolve-activity", "--brief", "-a", "android.intent.action.MAIN",
            "-c", "android.intent.category.LAUNCHER", PACKAGE,
        )
        launcher = output.splitlines()[-1]
        if not launcher.startswith(PACKAGE + "/"):
            raise RuntimeError("App launcher could not be resolved")
        self.shell("am", "start", "-n", launcher)

        def rendered(root):
            for node in root.iter("node"):
                if node.get("package") == PACKAGE and (node.get("text") or node.get("content-desc")):
                    return True
            return None

        self.screen(rendered, "the app's first labeled control", timeout=90)

    def uid(self):
        result = self.shell("cmd", "package", "list", "packages", "-U", PACKAGE)
        match = re.search(rf"^package:{re.escape(PACKAGE)} uid:(\d+)$", result, re.MULTILINE)
        if not match:
            raise RuntimeError("Could not read app UID")
        return match.group(1)

    def screen(self, predicate, description, *, timeout=45):
        deadline = time.monotonic() + timeout
        failures = {"valid": 0, "invalidXml": 0, "adbError": 0, "idleTimeout": 0}
        while time.monotonic() < deadline:
            try:
                self.shell("rm", "-f", self.xml_path)
                output = self.shell("uiautomator", "dump", self.xml_path)
                if "could not get idle state" in output.lower():
                    failures["idleTimeout"] += 1
                    continue
                root = ET.fromstring(self.adb("exec-out", "cat", self.xml_path))
                failures["valid"] += 1
                result = predicate(root)
                if result is not None:
                    return result
            except ET.ParseError:
                failures["invalidXml"] += 1
            except subprocess.CalledProcessError as error:
                if b"could not get idle state" in (error.stderr or b"").lower():
                    failures["idleTimeout"] += 1
                else:
                    failures["adbError"] += 1
            time.sleep(0.5)
        raise RuntimeError(f"Screen never showed {description}. UI hierarchy attempts: {failures}")

    def element(self, text=None, *, kind=None):
        def find(root):
            for node in root.iter("node"):
                if kind and node.get("class", "").rsplit(".", 1)[-1] != kind:
                    continue
                if text is None or text in (node.get("text"), node.get("content-desc")):
                    return node
            return None

        return self.screen(find, text or kind)

    @staticmethod
    def bounds(node):
        return tuple(map(int, re.findall(r"\d+", node.get("bounds", ""))))

    def tap(self, text=None, *, kind=None):
        left, top, right, bottom = self.bounds(self.element(text, kind=kind))
        self.shell("input", "tap", str((left + right) // 2), str((top + bottom) // 2))

    def queue_sample_inspection(self, button, name):
        self.tap(button, kind="Button")
        self.tap(kind="EditText")
        self.shell("input", "text", name)

        def entered(root):
            return next((node for node in root.iter("node")
                         if node.get("text") == name), None)

        self.screen(entered, "the inspection name")
        self.shell("input", "keyevent", "4")
        status = self.element("Status", kind="TextView")

        def picker(root):
            status_bottom = self.bounds(status)[3]
            candidates = [node for node in root.iter("node")
                          if node.get("class", "").endswith(".EditText")
                          and self.bounds(node)[1] >= status_bottom]
            return min(candidates, key=lambda node: self.bounds(node)[1]) if candidates else None

        left, top, right, bottom = self.bounds(self.screen(picker, "status picker"))
        self.shell("input", "tap", str((left + right) // 2), str((top + bottom) // 2))
        self.tap("New")
        # Small phones can leave Submit below the fold after dismissing the picker.
        def submit_visible(root):
            return any(node.get("text") == "Submit" and node.get("class") == "android.widget.Button"
                       for node in root.iter("node"))

        for _ in range(4):
            if self.screen(submit_visible, "form hierarchy"):
                break
            bounds = self.bounds(self.element(kind="ScrollView"))
            left, top, right, bottom = bounds
            x = (left + right) // 2
            self.shell("input", "swipe", str(x), str((top + 3 * bottom) // 4),
                       str(x), str((3 * top + bottom) // 4), "300")
        self.tap("Submit", kind="Button")
        self.tap("Navigate up")

    def outbox(self):
        def read(root):
            texts = [node.get("text", "") for node in root.iter("node")]
            summary = next((text for text in texts if text.startswith("Drafts ")), "")
            match = re.fullmatch(r"Drafts \d+ · Outbox (\d+) · Conflicts \d+ · Sent \d+", summary)
            ids = [s for s in texts if OUTBOX_RECORD.fullmatch(s)]
            if match and int(match.group(1)) == 1 and len(ids) == 1:
                return int(match.group(1)), ids[0]
            return None

        self.tap("Records", kind="FrameLayout")
        return self.screen(read, "exactly one Outbox record")

    def sketch(self, filename):
        image = self.adb("exec-out", "run-as", PACKAGE, "cat", f"files/media/{filename}")
        if not image.startswith(b"\x89PNG\r\n\x1a\n"):
            raise RuntimeError("Private sketch is not a PNG")
        return len(image), hashlib.sha256(image).hexdigest()


def run(device, legacy, current):
    if device.shell("getprop", "ro.kernel.qemu") != "1":
        raise RuntimeError("Use a disposable Android emulator")
    installed = device.shell("pm", "list", "packages", "-u", PACKAGE).splitlines()
    if f"package:{PACKAGE}" in installed:
        raise RuntimeError("App already installed. Use a fresh disposable emulator.")

    device.shell("cmd", "connectivity", "airplane-mode", "enable")
    device.shell("svc", "wifi", "disable")
    device.shell("svc", "data", "disable")
    device.install(legacy)
    for permission in ("ACCESS_COARSE_LOCATION", "ACCESS_FINE_LOCATION"):
        device.shell("pm", "grant", PACKAGE, f"android.permission.{permission}")
    device.launch()
    device.element("Honua Collect", kind="TextView")
    uid = device.uid()

    device.tap("Capture Widgets Demo", kind="Button")
    device.tap("Sketch", kind="Button")
    _, toolbar, right, _ = device.bounds(device.element("Save", kind="Button"))
    device.shell("input", "swipe", str(right // 4), str(toolbar // 3),
                 str(right * 3 // 4), str(toolbar // 2), "400")
    device.tap("Save", kind="Button")
    device.element("1 attached", kind="TextView")
    files = device.shell("run-as", PACKAGE, "ls", "files/media").splitlines()
    sketches = [name for name in files if re.fullmatch(r"[0-9a-f]{32}\.png", name)]
    if len(sketches) != 1:
        raise RuntimeError("Expected exactly one private sketch file")
    filename = sketches[0]
    sketch = device.sketch(filename)
    device.tap("Navigate up")

    device.queue_sample_inspection("Start New Inspection", "UpgradeOutbox")
    before = device.outbox()

    device.shell("am", "force-stop", PACKAGE)
    device.install(current, update=True)
    device.launch()
    device.element("Honua Field", kind="TextView")
    if device.uid() != uid:
        raise RuntimeError("App UID changed after the in-place upgrade")
    if device.outbox() != before:
        raise RuntimeError("Outbox count or record ID changed after upgrade")
    if device.sketch(filename) != sketch:
        raise RuntimeError("Legacy sketch bytes changed after upgrade")

    print(f"Honua Field launcher opened with the same Android app UID {uid}.")
    print(f"Preserved {before[0]} Outbox record with ID {before[1]}.")
    print(f"Preserved the {sketch[0]}-byte legacy sketch (SHA-256 {sketch[1]}).")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("serial", help="adb serial for a fresh disposable emulator")
    parser.add_argument("legacy_apk", type=Path, help="signed debuggable Honua Collect APK")
    parser.add_argument("field_apk", type=Path, help="signed debuggable Honua Field APK")
    parser.add_argument("--adb", default="adb", help="path to adb")
    args = parser.parse_args()
    for apk in (args.legacy_apk, args.field_apk):
        if not apk.is_file():
            parser.error(f"APK does not exist: {apk}")
    try:
        run(Android(args.adb, args.serial), args.legacy_apk.resolve(), args.field_apk.resolve())
    except (OSError, RuntimeError, subprocess.SubprocessError) as error:
        parser.exit(1, f"Installed upgrade check failed: {error}\n")


if __name__ == "__main__":
    main()
