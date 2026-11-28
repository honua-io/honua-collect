"""Seed one scoped assignment and write no credentials to stdout."""

import base64
import hashlib
import hmac
import json
from pathlib import Path
import ssl
import sys
import time
from urllib.parse import urlencode
from urllib.request import Request, urlopen
from uuid import uuid4


def segment(value):
    return base64.urlsafe_b64encode(value).rstrip(b"=")


def main(directory):
    base = "https://127.0.0.1:5191"
    username = "pilot-crew-a"
    service = "svc-field-inspections-feature"
    now = int(time.time())
    header = segment(b'{"alg":"HS256","typ":"JWT"}')
    claims = segment(json.dumps({
        "iss": "https://fixture.invalid", "aud": "honua-fixture", "sub": username,
        "roles": ["editor"], "nbf": now - 10, "exp": now + 1800,
    }).encode())
    signature = segment(hmac.digest((directory / "signing").read_bytes().strip(),
                                    header + b"." + claims, hashlib.sha256))
    jwt = b".".join((header, claims, signature)).decode()
    (directory / "worker.json").write_text(json.dumps({"username": username, "password": jwt}))
    context = ssl.create_default_context(cafile=str(directory / "tls.crt"))

    def post(route, data, headers):
        with urlopen(Request(base + route, data, headers), context=context, timeout=20) as response:
            return json.load(response)

    token = post("/sharing/rest/generateToken", urlencode({
        "username": username, "password": jwt, "client": "requestip", "f": "json",
    }).encode(), {"Content-Type": "application/x-www-form-urlencoded"})["token"]
    task_id = str(uuid4())
    task = post("/api/v1/tasks", json.dumps({
        "commandId": str(uuid4()), "taskId": task_id, "type": "request", "serviceId": "field_inspections",
        "formId": "field_runtime_inspection",
        "title": "Installed Android assignment fixture",
        "location": {"type": "Point", "coordinates": [-156.47, 20.89]},
    }).encode(), {"Authorization": "Bearer " + token, "Content-Type": "application/json"})["task"]
    if task["serviceId"] != service:
        raise ValueError("Unexpected canonical task service")
    actor = task["createdBy"]
    assigned = post(f"/api/v1/tasks/{task_id}/transitions", json.dumps({
        "commandId": str(uuid4()), "expectedVersion": task["version"], "action": "assign", "assignee": actor,
    }).encode(), {"X-API-Key": (directory / "admin").read_text().strip(),
                  "Content-Type": "application/json"})["task"]
    if assigned["taskId"] != task_id or assigned["assignee"] != actor:
        raise ValueError("Assigned task did not match")
    print(json.dumps({"taskId": task_id, "serviceId": service, "assignee": actor}))


if __name__ == "__main__":
    try:
        main(Path(sys.argv[1]))
    except (OSError, ValueError, KeyError) as error:
        raise SystemExit(f"Assignment fixture setup failed ({type(error).__name__})") from None
