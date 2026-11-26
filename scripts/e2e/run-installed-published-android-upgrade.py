#!/usr/bin/env python3
"""Seed on an earlier Field Debug APK, then verify an in-place update offline.

Use a fresh, unlocked emulator, the published Android E2E fixture, and
standalone APKs signed by the same key. The candidate must increase Android
versionCode. Put adb, apksigner, Java, and Maestro on PATH. ADB, APKSIGNER,
MAESTRO, and HONUA_E2E_HTTP_PORT can override defaults.

Run seed with an earlier APK and fixture worker key file. Run verify with the
same serial and report directory and the current APK. Both phases leave the
disposable emulator installation and its evidence intact.

Run recover-key after verify to temporarily hide and restore the test app's
Android SecureStorage preferences while checking the database stays intact.
"""
import argparse
import hashlib
import json
import os
import re
import runpy
import shlex
import subprocess
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
legacy = runpy.run_path(str(HERE / "run-installed-android-upgrade.py"))
Android, PACKAGE = legacy["Android"], legacy["PACKAGE"]
FORM = "field_runtime_inspection"
DRAFT_NAME = "Upgrade saved inspection"
QUEUE_NAME = "Upgrade queued inspection"
SUMMARY = "Drafts 1 · Outbox 1 · Conflicts 0 · Sent 0"
SECURE_PREFS = f"shared_prefs/{PACKAGE}.microsoft.maui.essentials.preferences.xml"
MISSING_KEY = "The device key for existing field data is unavailable."


def check(condition, message):
    if not condition:
        raise RuntimeError(message)


def stop(device):
    device.shell("am", "force-stop", PACKAGE)


def installed_digest(device):
    path = device.shell("pm", "path", PACKAGE).removeprefix("package:")
    return device.shell("sha256sum", path).split()[0]


def installed(device, apk):
    digest = installed_digest(device)
    with apk.open("rb") as stream:
        check(digest == hashlib.file_digest(stream, "sha256").hexdigest(), "Installed APK differs")
    package = device.shell("dumpsys", "package", PACKAGE)
    certificate = subprocess.check_output(
        [os.environ.get("APKSIGNER", "apksigner"), "verify", "--print-certs", str(apk)],
        text=True,
    )
    return {
        "package": PACKAGE,
        "uid": device.uid(),
        "versionCode": int(re.search(r"\bversionCode=(\d+)", package)[1]),
        "versionName": re.search(r"\bversionName=(\S+)", package)[1],
        "apkSha256": digest,
        "certificateSha256": re.search(r"certificate SHA-256 digest: ([a-f0-9]+)", certificate)[1],
    }


def run_flow(args, phase, flow, **variables):
    command = [
        os.environ.get("MAESTRO", "maestro"), "--device", args.serial, "test",
        "--format", "junit", "--output", str(args.report / f"{phase}-{flow}.xml"),
        *[item for key, value in variables.items() for item in ("-e", f"{key}={value}")],
        str(HERE / "maestro" / f"{flow}.yaml"),
    ]
    subprocess.run(command, check=True, timeout=360)


def capture_failure(args, device):
    def probe(*command):
        try:
            return subprocess.check_output([*device.command, *command],
                                           stderr=subprocess.PIPE, timeout=10)
        except (OSError, subprocess.SubprocessError):
            return b""

    screenshot = probe("exec-out", "screencap", "-p")
    if screenshot.startswith(b"\x89PNG\r\n\x1a\n"):
        (args.report / "failure.png").write_bytes(screenshot)
    # Check process and focus without recording app data or unrestricted logs.
    state = probe("get-state").strip()
    pid = probe("shell", "pidof", PACKAGE).strip()
    windows = probe("shell", "dumpsys", "window", "windows").splitlines()
    focus = next((line for line in windows if b"mCurrentFocus=" in line), b"")
    evidence = {
        "phase": args.phase,
        "deviceConnected": state == b"device",
        "appProcessRunning": bool(re.fullmatch(rb"[0-9]+( [0-9]+)*", pid)),
        "appWindowFocused": PACKAGE.encode() + b"/" in focus if focus else None,
    }
    (args.report / "failure-state.json").write_text(json.dumps(evidence, indent=2) + "\n")


def records(device):
    stop(device)
    device.launch()
    device.tap("Records", kind="FrameLayout")

    def read(root):
        labels = [node.get("text", "") for node in root.iter("node")]
        ids = [label for label in labels if re.fullmatch(r"[0-9a-f]{32}", label)]
        if SUMMARY in labels and "Ready to send" in labels and len(ids) == 2:
            return {"draftId": ids[0], "outboxId": ids[1], "summary": SUMMARY}
        return None

    try:
        return device.screen(read, SUMMARY + " and two record IDs")
    except RuntimeError as error:
        observed = {}

        def describe(root):
            nodes = [node for node in root.iter("node") if node.get("package") == PACKAGE]
            labels = [node.get("text", "") for node in nodes]
            observed.update(
                recordCount=sum(bool(re.fullmatch(r"[0-9a-f]{32}", label)) for label in labels),
                matchingLabels=[label for label in labels if label in {
                    "Drafts", "Outbox", "Sent", "No drafts.", "Outbox is empty.",
                    "Nothing sent yet.", "Ready to send", "Retry",
                } or re.fullmatch(r"Drafts \d+ · Outbox \d+ · Conflicts \d+ · Sent \d+", label)],
                missingKey=any(label.startswith(MISSING_KEY) for label in labels),
                appNodeCount=len(nodes),
            )
            return True

        try:
            device.screen(describe, "a diagnostic UI hierarchy", timeout=5)
        except RuntimeError:
            pass
        raise RuntimeError(f"{error}. Final app UI: {json.dumps(observed)}") from error


def private_files(device):
    def digest(path):
        content = device.adb("exec-out", "run-as", PACKAGE, "cat", path)
        check(bool(content), f"{path} was empty")
        return hashlib.sha256(content).hexdigest()

    cache = device.shell("run-as", PACKAGE, "find", "files", "-maxdepth", "1",
                         "-name", shlex.quote("*.form.json")).splitlines()
    check(len(cache) == 1, "Expected one cached published form")
    payload = json.loads(device.adb("exec-out", "run-as", PACKAGE, "cat", cache[0]))
    media = device.shell("run-as", PACKAGE, "find", "files/media", "-type", "f").splitlines()
    check(len(media) == 2, "Expected one private attachment for each record")
    return {
        "formVersion": payload["version"],
        "formSha256": digest(cache[0]),
        "mediaSha256": {path: digest(path) for path in sorted(media)},
    }


def snapshot(args, device, phase):
    result = records(device)
    stop(device)
    run_flow(args, phase, "upgrade-saved-draft", DRAFT_ID=result["draftId"],
             INSPECTION_NAME=DRAFT_NAME)
    stop(device)
    return result | private_files(device)


def write_report(args, phase, evidence):
    path = args.report / f"{phase}.json"
    path.write_text(json.dumps(evidence, indent=2) + "\n")
    print(path.read_text(), flush=True)


def seed(args, device):
    check(not device.shell("pm", "list", "packages", PACKAGE), "Use a fresh disposable emulator")
    device.install(args.apk)
    identity = installed(device, args.apk)
    port = os.environ.get("HONUA_E2E_HTTP_PORT", "5189")
    endpoint = f"http://127.0.0.1:{port}"
    key = args.key_file.read_text().strip()
    check(bool(key), "The worker key file is empty")
    request = urllib.request.Request(f"{endpoint}/api/v1/forms/{FORM}", headers={"X-API-Key": key})
    with urllib.request.urlopen(request, timeout=15) as response:
        form = json.load(response)
    check(form["package"]["offlinePolicy"]["enabled"], "The fixture must allow offline capture")
    settings = {"server": {"baseUrl": endpoint, "formId": FORM}, "demo": {"apiKey": key}}
    subprocess.run([*device.command, "shell",
                    f"run-as {PACKAGE} sh -c 'mkdir -p files && cat > files/appsettings.local.json'"],
                   input=json.dumps(settings), text=True, check=True, timeout=15)

    for permission in ("ACCESS_COARSE_LOCATION", "ACCESS_FINE_LOCATION"):
        device.shell("pm", "grant", PACKAGE, f"android.permission.{permission}")
    container = os.environ.get("HONUA_E2E_EMULATOR_CONTAINER")
    if container:
        subprocess.run(["docker", "exec", container, "adb", "-s", "emulator-5554",
                        "emu", "geo", "fix", "-157.81", "21.31"], check=True, timeout=15)
    else:
        device.adb("emu", "geo", "fix", "-157.81", "21.31")

    device.adb("reverse", f"tcp:{port}", f"tcp:{port}")
    try:
        run_flow(args, "before", "download", FORM_VERSION=form["version"])
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
        try:
            device.adb("reverse", "--remove", f"tcp:{port}")
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
            print("Emulator reverse cleanup also failed after download failure.", flush=True)
        raise
    device.adb("reverse", "--remove", f"tcp:{port}")

    for flow, name in (("capture", QUEUE_NAME), ("resume-draft", QUEUE_NAME), ("capture", DRAFT_NAME)):
        stop(device)
        run_flow(args, "before-queued" if name == QUEUE_NAME else "before-saved", flow, INSPECTION_NAME=name)
    evidence = snapshot(args, device, "before")
    check(evidence["formVersion"] == form["version"], "Cached form version differs from downloaded version")
    write_report(args, "before", {"identity": identity, "storage": evidence})


def verify(args, device):
    before = json.loads((args.report / "before.json").read_text())
    check(device.uid() == before["identity"]["uid"], "Historical app UID changed before update")
    check(installed_digest(device) == before["identity"]["apkSha256"], "Historical APK changed before update")
    port = os.environ.get("HONUA_E2E_HTTP_PORT", "5189")
    check(f"tcp:{port} " not in device.adb("reverse", "--list").decode(),
          "Remove the fixture's ADB reverse port before upgrading")
    stop(device)
    device.install(args.apk, update=True)
    identity = installed(device, args.apk)
    for key in ("package", "uid", "certificateSha256"):
        check(before["identity"][key] == identity[key], f"Installed {key} changed after update")
    check(before["identity"]["apkSha256"] != identity["apkSha256"], "Candidate must be a different APK")
    check(before["identity"]["versionCode"] < identity["versionCode"], "Candidate must increase Android versionCode")
    evidence = snapshot(args, device, "after")
    check(evidence == before["storage"], "Historical draft, outbox, cached package, or media changed")
    write_report(args, "after", {"identity": identity, "storage": evidence})


def database_digests(device):
    paths = device.shell("run-as", PACKAGE, "find", "files", "-maxdepth", "1", "-name",
                         shlex.quote("collect-*.db*")).splitlines()
    check("files/collect-records.db" in paths, "Existing record database was absent")
    return {path: device.shell("run-as", PACKAGE, "sha256sum", path).split()[0]
            for path in sorted(paths)}


def recover_key(args, device):
    after = json.loads((args.report / "after.json").read_text())
    check(installed_digest(device) == after["identity"]["apkSha256"], "Installed candidate changed")
    check(device.uid() == after["identity"]["uid"], "Installed app UID changed")
    stop(device)
    databases = database_digests(device)
    files = private_files(device)
    backup = SECURE_PREFS + ".upgrade-backup"
    device.shell("run-as", PACKAGE, "test", "!", "-e", backup)
    device.shell("run-as", PACKAGE, "mv", SECURE_PREFS, backup)

    def blocked(root):
        visible = {node.get("text", "") for node in root.iter("node")}
        return True if "Retry" in visible and any(text.startswith(MISSING_KEY) for text in visible) else None

    try:
        device.launch()
        device.screen(blocked, "missing-key recovery message and Retry")
        device.tap("Retry")
        device.screen(blocked, "missing-key recovery message after Retry")
        check(database_digests(device) == databases, "Missing-key startup changed an existing database")
        check(private_files(device) == files, "Missing-key startup changed a cache or attachment")
    finally:
        try:
            stop(device)
        finally:
            device.shell("run-as", PACKAGE, "mv", "-f", backup, SECURE_PREFS)

    recovered = snapshot(args, device, "recovered-key")
    check(recovered == after["storage"], "Offline records changed after restoring the original key")
    write_report(args, "recovered-key", {"databaseSha256WhileKeyMissing": databases, "storage": recovered})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("serial")
    parser.add_argument("report", type=Path)
    phases = parser.add_subparsers(dest="phase", required=True)
    seed_args = phases.add_parser("seed")
    seed_args.add_argument("apk", type=Path)
    seed_args.add_argument("key_file", type=Path)
    phases.add_parser("verify").add_argument("apk", type=Path)
    phases.add_parser("recover-key")
    args = parser.parse_args()
    args.report = args.report.resolve()
    args.report.mkdir(parents=True, exist_ok=True)
    device = Android(os.environ.get("ADB", "adb"), args.serial)
    check(device.shell("getprop", "ro.kernel.qemu") == "1", "An Android emulator is required")
    try:
        {"seed": seed, "verify": verify, "recover-key": recover_key}[args.phase](args, device)
    except Exception:
        try:
            capture_failure(args, device)
        except OSError:
            print("Could not write emulator failure evidence.", flush=True)
        raise


if __name__ == "__main__":
    main()
