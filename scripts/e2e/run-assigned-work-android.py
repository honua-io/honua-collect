#!/usr/bin/env python3
"""Verify a hosted pilot APK signs in and refreshes assignments after restart.

Use a disposable, unlocked Android emulator without Field installed. Trust the
fixture CA on the emulator before running. The script temporarily routes the
APK endpoint through a local TLS observer using the fixture certificate and
private key. Seed exactly one task assigned to the named non-admin worker.

Pass a mode-0600 JSON credentials file with username and password properties.
Use a short-lived test JWT for password. The script leaves the APK installed
on the emulator. The report records counts, the expected task ID and the installed APK digest.

Example:
    scripts/e2e/run-assigned-work-android.py --serial 127.0.0.1:5573 \
        --apk /path/to/hosted.apk --input-apk /path/to/fixture-input.apk --tls-key /private/fixture.key \
        --url https://127.0.0.1:5191 --ca /path/to/ca.crt --tls-cert /path/to/fixture.crt \
        --service svc-field-inspections-feature --task <seeded-task-uuid> --assignee <canonical-actor> \
        --credentials /private/worker.json --report /path/to/report.json
"""

import argparse
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import runpy
import ssl
import subprocess
import threading
import time
from urllib.parse import parse_qs, urlencode, urlsplit
from urllib.error import HTTPError
from urllib.request import HTTPRedirectHandler, HTTPSHandler, Request, build_opener, urlopen

ANDROID = runpy.run_path(str(Path(__file__).with_name("run-installed-android-upgrade.py")))
Android = ANDROID["Android"]
PACKAGE = ANDROID["PACKAGE"]


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def has_assignment(body, args):
    items = body.get("items", [])
    return (
        len(items) == 1
        and items[0].get("taskId") == args.task
        and items[0].get("assignee") == args.assignee
        and not body.get("nextCursor")
    )


def readback(args, username, password):
    context = ssl.create_default_context(cafile=str(args.ca))
    base = args.url.rstrip("/")

    def request(route, data=None, headers=None):
        with urlopen(Request(base + route, data, headers or {}), context=context, timeout=15) as response:
            return json.load(response)

    token = request("/sharing/rest/generateToken", urlencode({
        "username": username, "password": password, "client": "requestip", "f": "json",
    }).encode()).get("token")
    if not token:
        raise RuntimeError("Fixture worker failed token exchange")
    headers = {"Authorization": f"Bearer {token}"}
    profile = request("/sharing/rest/community/self?f=json", headers=headers)
    if profile.get("username") != username or profile.get("actorId") != args.assignee:
        raise RuntimeError("Fixture returned an unexpected account")
    query = urlencode({"serviceId": args.service, "assignee": args.assignee})
    if not has_assignment(request("/api/v1/tasks?" + query, headers=headers), args):
        raise RuntimeError("Fixture worker did not read exactly the expected assignment")


def enter(android, input_apk, username, password):
    package = "io.honua.fixtureinput"
    if android.shell("pm", "list", "packages", package):
        raise RuntimeError("Use an emulator without the credential entry helper installed")
    android.adb("install", "--no-streaming", str(input_apk))
    try:
        command = f'run-as {package} sh -c "mkdir -p files && cat > files/fixture"'
        subprocess.run(
            [*android.command, "shell", "-T", command],
            input=f"{username}\n{password}\n", text=True, capture_output=True, timeout=30, check=True,
        )
        result = android.shell("am", "instrument", "-w", f"{package}/.FixtureInput")
        if "root=io.honua.collect" not in result or "inputCount=2" not in result:
            raise RuntimeError("Credential helper did not fill both Field inputs")
    finally:
        android.adb("uninstall", package)


class Observer:
    def __init__(self, args):
        self.lock = threading.Lock()
        self.exchange_count = 0
        self.exchange_denied = False
        self.refreshes = 0
        self.bearer_digest = None
        self.verified_profile = False
        self.mismatch = False
        context = ssl.create_default_context(cafile=str(args.ca))
        opener = build_opener(HTTPSHandler(context=context), NoRedirect())
        observer = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_POST(self):
                self.forward()

            def do_GET(self):
                self.forward()

            def forward(self):
                path = urlsplit(self.path)
                if path.path not in {
                    "/sharing/rest/generateToken", "/sharing/rest/community/self", "/api/v1/tasks",
                }:
                    self.send_error(404)
                    return
                headers = {name: self.headers[name] for name in ("Authorization", "Content-Type")
                           if name in self.headers}
                try:
                    size = int(self.headers.get("Content-Length", "0"))
                    if not 0 <= size <= 65536:
                        self.send_error(413)
                        return
                    data = self.rfile.read(size) if self.command == "POST" else None
                    request = Request(args.url.rstrip("/") + self.path, data, headers, method=self.command)
                    try:
                        response = opener.open(request, timeout=15)
                    except HTTPError as error:
                        response = error
                    with response:
                        encoded = response.read()
                        if response.status == 200:
                            observer.observe(path, json.loads(encoded), headers.get("Authorization", ""), args)
                        self.send_response(response.status)
                        self.send_header("Content-Type", response.headers.get("Content-Type", "application/json"))
                        self.send_header("Content-Length", str(len(encoded)))
                        self.end_headers()
                        self.wfile.write(encoded)
                except (OSError, ValueError, TypeError):
                    self.close_connection = True

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        front = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        front.load_cert_chain(args.tls_cert, args.tls_key)
        self.server.socket = front.wrap_socket(self.server.socket, server_side=True)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)

    def observe(self, path, body, authorization, args):
        with self.lock:
            if path.path == "/sharing/rest/generateToken":
                if isinstance(body.get("token"), str) and body["token"]:
                    self.exchange_count += 1
                    self.bearer_digest = hashlib.sha256(("Bearer " + body["token"]).encode()).digest()
                else:
                    self.exchange_denied = True
                return
            same_bearer = self.bearer_digest == hashlib.sha256(authorization.encode()).digest()
            if path.path == "/sharing/rest/community/self":
                self.verified_profile = same_bearer and body.get("actorId") == args.assignee
            elif same_bearer and self.verified_profile and has_assignment(body, args):
                query = parse_qs(path.query)
                if query.get("serviceId") == [args.service] and query.get("assignee") == [args.assignee]:
                    self.refreshes += 1
                    return
                self.mismatch = True
            else:
                self.mismatch = True

    def wait(self, minimum):
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            with self.lock:
                if self.exchange_denied:
                    raise RuntimeError("Native fixture token exchange was denied")
                if self.mismatch or self.exchange_count > 1:
                    raise RuntimeError("Native refresh used an unexpected worker or assignment")
                if self.exchange_count == 1 and self.refreshes >= minimum:
                    return self.refreshes
            time.sleep(0.25)
        raise RuntimeError(f"Native refresh timed out (exchanges={self.exchange_count}, "
                           f"profile={self.verified_profile}, refreshes={self.refreshes}, expected={minimum})")


def run(args):
    url = urlsplit(args.url)
    if (url.scheme != "https" or url.hostname != "127.0.0.1" or not url.port
            or url.username or url.password or url.path not in ("", "/") or url.query or url.fragment
            or args.credentials.stat().st_mode & 0o077 or args.tls_key.stat().st_mode & 0o077):
        raise RuntimeError("Use a loopback HTTPS URL and private credentials and TLS key files")
    credentials = json.loads(args.credentials.read_text())
    username, password = credentials["username"], credentials["password"]
    readback(args, username, password)
    android = Android(args.adb, args.serial)
    if android.shell("getprop", "ro.kernel.qemu") != "1":
        raise RuntimeError("A disposable emulator is required")
    if android.shell("pm", "list", "packages", PACKAGE):
        raise RuntimeError("Use an emulator without Field installed")
    observer = Observer(args)
    device_port = f"tcp:{url.port}"
    reverse = [line.split() for line in android.adb("reverse", "--list").decode().splitlines()]
    previous = next((mapping[2] for mapping in reverse if len(mapping) == 3 and mapping[1] == device_port), None)
    if previous:
        raise RuntimeError("The disposable emulator already has a fixture port mapping")
    observer.thread.start()
    try:
        android.adb("reverse", device_port, f"tcp:{observer.server.server_port}")
        if "Success" not in android.adb("install", "--no-streaming", str(args.apk)).decode():
            raise RuntimeError("Field installation failed")
        android.launch()
        android.tap("More", kind="FrameLayout")
        android.tap("Account")
        enter(android, args.input_apk, username, password)
        android.tap("Sign in", kind="Button")
        observer.wait(1)
        android.shell("am", "force-stop", PACKAGE)
        with observer.lock:
            first = observer.refreshes
        android.launch()
        resumed = observer.wait(first + 1) - first
        readback(args, username, password)
        with args.apk.open("rb") as apk:
            digest = hashlib.file_digest(apk, "sha256").hexdigest()
        installed = android.shell("pm", "path", PACKAGE).removeprefix("package:")
        if android.shell("sha256sum", installed).split()[0] != digest:
            raise RuntimeError("Installed APK digest differed from the provided APK")
        args.report.write_text(json.dumps({
            "apkSha256": digest,
            "initialAuthenticatedRefreshes": first,
            "relaunchAuthenticatedRefreshes": resumed,
            "workerAssignmentReadbacks": 2,
            "taskId": args.task,
        }, indent=2) + "\n")
    finally:
        try:
            android.shell("rm", "-f", android.xml_path)
            android.adb("reverse", "--remove", device_port)
        finally:
            observer.server.shutdown()
            observer.server.server_close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", default="adb")
    for option in ("serial", "url", "service", "task", "assignee"):
        parser.add_argument("--" + option, required=True)
    for option in ("apk", "input-apk", "ca", "tls-cert", "tls-key", "credentials", "report"):
        parser.add_argument("--" + option, type=Path, required=True)
    try:
        run(parser.parse_args())
    except (RuntimeError, OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        # Suppress raw protocol, device and subprocess messages.
        raise SystemExit(str(error) if type(error) is RuntimeError else type(error).__name__) from None


if __name__ == "__main__":
    main()
