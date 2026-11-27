import concurrent.futures
import hashlib
import json
import os
import re
import subprocess
import time
import uuid
from pathlib import Path

REGION = "us-west-2"
DEFAULT_DEVICE_ARN = (
    "arn:aws:devicefarm:us-west-2::device:1ED2EBCF5578549493A578A98C1956B3"
)
UPLOADS = (
    ("field-live.ipa", "IOS_APP"),
    ("field-ios-honua-live.zip", "APPIUM_PYTHON_TEST_PACKAGE"),
    ("live.yml", "APPIUM_PYTHON_TEST_SPEC"),
)


def devicefarm(*args):
    return json.loads(
        subprocess.check_output(
            ["aws", "devicefarm", *args, "--region", REGION, "--output", "json"]
        )
    )


def upload_artifact(project_arn, artifact_path, artifact_type):
    upload = devicefarm(
        "create-upload",
        "--project-arn", project_arn,
        "--name", artifact_path,
        "--type", artifact_type,
    )["upload"]
    transfer = subprocess.run(
        [
            "curl", "-fsS", "--retry", "2", "--retry-all-errors", "--max-time", "900",
            "--upload-file", artifact_path, "--config", "-",
        ],
        input=f"url = {json.dumps(upload['url'])}\n",
        text=True,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    if transfer.returncode:
        raise RuntimeError(f"{artifact_type} transfer failed (curl exit {transfer.returncode})")

    upload_arn = upload["arn"]
    for _ in range(200):
        upload_status = devicefarm("get-upload", "--arn", upload_arn)["upload"]
        if upload_status["status"] == "SUCCEEDED":
            return upload_arn
        if upload_status["status"] == "FAILED":
            raise RuntimeError(f"{artifact_type} failed: {upload_status.get('message')}")
        time.sleep(3)
    raise TimeoutError(f"Timed out validating {artifact_type}")


def app_checksum():
    with Path(UPLOADS[0][0]).open("rb") as app:
        return hashlib.file_digest(app, "sha256").hexdigest()


def reuse_app_upload(project_arn, receipt_file, checksum):
    receipt = json.loads(Path(receipt_file).read_text())
    if receipt["sha256"] != checksum:
        raise ValueError("Previous upload receipt does not match the staged IPA")
    upload_arn = receipt["uploadArn"]
    project_prefix = project_arn.replace(":project:", ":upload:") + "/"
    if not upload_arn.startswith(project_prefix):
        raise ValueError("Previous app upload does not belong to the configured project")
    upload = devicefarm("get-upload", "--arn", upload_arn)["upload"]
    if upload["type"] != "IOS_APP" or upload["status"] != "SUCCEEDED":
        raise ValueError("Previous upload must be a validated iOS app")
    return upload_arn


def main():
    previous_revision, field_revision, server_revision, upgrade_state = (
        Path("provenance.txt").read_text().splitlines()
    )
    revisions = (previous_revision, field_revision, server_revision)
    if not all(re.fullmatch(r"[0-9a-f]{40}", revision) for revision in revisions):
        raise ValueError("Invalid staged Git revision")
    if upgrade_state not in {"draft", "outbox"}:
        raise ValueError("Invalid staged upgrade state")
    project_arn = Path(os.environ["HONUA_DEVICEFARM_PROJECT_FILE"]).read_text().strip()
    device_arn = os.environ.get("HONUA_DEVICEFARM_DEVICE_ARN", DEFAULT_DEVICE_ARN)
    run_name = (
        f"Field iOS {upgrade_state} {previous_revision[:8]}-{field_revision[:8]} "
        f"Server {server_revision[:8]} {uuid.uuid4().hex[:12]}"
    )

    checksum = app_checksum()
    receipt_file = os.environ.get("HONUA_DEVICEFARM_APP_RECEIPT")
    if receipt_file:
        app_arn = reuse_app_upload(project_arn, receipt_file, checksum)
    else:
        app_path, app_type = UPLOADS[0]
        app_arn = upload_artifact(project_arn, app_path, app_type)
    Path("app-upload.json").write_text(
        json.dumps({"uploadArn": app_arn, "sha256": checksum}, indent=2) + "\n"
    )
    test_uploads = UPLOADS[1:]
    with concurrent.futures.ThreadPoolExecutor(max_workers=len(test_uploads)) as executor:
        upload_futures = [
            executor.submit(upload_artifact, project_arn, artifact_path, artifact_type)
            for artifact_path, artifact_type in test_uploads
        ]
        test_package_arn, test_spec_arn = [future.result() for future in upload_futures]

    request = {
        "projectArn": project_arn,
        "appArn": app_arn,
        "deviceSelectionConfiguration": {
            "filters": [{"attribute": "ARN", "operator": "IN", "values": [device_arn]}],
            "maxDevices": 1,
        },
        "name": run_name,
        "test": {
            "type": "APPIUM_PYTHON",
            "testPackageArn": test_package_arn,
            "testSpecArn": test_spec_arn,
        },
        "executionConfiguration": {"skipAppResign": True, "jobTimeoutMinutes": 45},
        "configuration": {"billingMethod": "METERED"},
    }
    Path("request.json").write_text(json.dumps(request, indent=2) + "\n")
    run_arn = devicefarm("schedule-run", "--cli-input-json", "file://request.json")["run"]["arn"]
    Path("runarn").write_text(run_arn + "\n")
    print(run_arn)


if __name__ == "__main__":
    main()
