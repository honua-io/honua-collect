"""AWS Device Farm gate for a Field update over an existing queued inspection."""
import hashlib
import json
import os
from pathlib import Path
import re
import runpy


def test_android_upgrade():
    bundle = Path(os.environ["DEVICEFARM_TEST_PACKAGE_PATH"])
    logs = Path(os.environ["DEVICEFARM_LOG_DIR"])
    legacy = runpy.run_path(str(bundle / "run-installed-android-upgrade.py"))
    package = legacy["PACKAGE"]
    device = legacy["Android"]("adb", os.environ["DEVICEFARM_DEVICE_UDID"])
    candidate = bundle / "candidate.apk"

    def installed(apk):
        remote = device.shell("pm", "path", package).removeprefix("package:")
        digest = device.shell("sha256sum", remote).split()[0]
        with apk.open("rb") as stream:
            assert digest == hashlib.file_digest(stream, "sha256").hexdigest(), "Installed APK differs"
        info = device.shell("dumpsys", "package", package)
        return {"apkSha256": digest, "versionCode": int(re.search(r"\bversionCode=(\d+)", info)[1]),
                "uid": device.uid()}

    report = {"device": device.shell("getprop", "ro.product.model"), "os": os.environ["DEVICEFARM_DEVICE_OS_VERSION"],
              "runArn": os.environ["DEVICEFARM_RUN_ARN"], "outcome": "failed"}
    try:
        # Device Farm supplies the historical APK. Read the installed bytes before seeding it.
        previous = Path(os.environ["DEVICEFARM_APP_PATH"])
        if not device.shell("pm", "path", package):
            assert b"Success" in device.adb("install", str(previous))
        report["previous"] = installed(previous)
        device.launch()
        device.tap("Records", kind="FrameLayout")
        device.element("Outbox is empty.")
        device.tap("Capture", kind="FrameLayout")
        device.queue_sample_inspection("Sample Inspection Demo", "PhysicalUpgrade")
        _, record = device.outbox()
        report["previous"]["outboxRecordId"] = record
        (logs / "before.png").write_bytes(device.adb("exec-out", "screencap", "-p"))

        device.shell("am", "force-stop", package)
        assert b"Success" in device.adb("install", "-r", str(candidate)), "In-place update failed"
        report["candidate"] = installed(candidate)
        assert report["candidate"]["versionCode"] > report["previous"]["versionCode"]
        assert report["candidate"]["uid"] == report["previous"]["uid"]
        device.launch()
        count, recovered = device.outbox()
        report["candidate"]["outboxRecordId"] = recovered
        assert count == 1 and recovered == record, "Queued record changed across update"
        (logs / "after.png").write_bytes(device.adb("exec-out", "screencap", "-p"))
        report["outcome"] = "passed"
    finally:
        (logs / "upgrade.json").write_text(json.dumps(report, indent=2) + "\n")
