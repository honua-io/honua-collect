#!/usr/bin/env python3
"""Verify one real Honua Field capture through FeatureServer and PostGIS."""

import argparse
import hashlib
import json
import math
import os
import subprocess
from pathlib import Path
from urllib.parse import quote, urlencode
from urllib.request import Request, urlopen


def require(condition, message):
    if not condition:
        raise SystemExit(message)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", default="http://127.0.0.1:5189")
    parser.add_argument("--service", default="field_inspections")
    parser.add_argument("--layer", type=int, default=68950)
    parser.add_argument("--form", default="field_runtime_inspection")
    parser.add_argument("--version", type=int)
    parser.add_argument("--latest-version", type=int, help="Newer version published while the device was offline")
    parser.add_argument("--name", required=True)
    parser.add_argument("--status", action="append", required=True)
    parser.add_argument("--total", type=int, required=True)
    parser.add_argument("--repeat-note", action="append", required=True)
    parser.add_argument("--longitude", type=float, required=True)
    parser.add_argument("--latitude", type=float, required=True)
    parser.add_argument("--attachment", type=Path, required=True)
    parser.add_argument("--report", type=Path, help="Write verified server readback evidence as JSON")
    parser.add_argument("--api-key-file", type=Path)
    parser.add_argument("--db-container", default=os.environ.get("HONUA_E2E_PG_CONTAINER", "honua-field-xlsform-pg"))
    parser.add_argument("--db-name", default="honua")
    parser.add_argument("--db-user", default="postgres")
    args = parser.parse_args()
    key = (args.api_key_file.read_text().strip() if args.api_key_file
           else os.environ.get("HONUA_E2E_APIKEY", ""))
    headers = {"X-API-Key": key} if key else {}
    server = args.server.rstrip("/")

    def get(path, params=None):
        url = server + path + ("?" + urlencode(params) if params else "")
        with urlopen(Request(url, headers=headers), timeout=20) as response:
            return response.read()

    def get_json(path, params=None):
        result = json.loads(get(path, params or {"f": "json"}))
        require("error" not in result, f"Honua request failed for {path}: {result.get('error')}")
        return result

    version = args.version
    if version is None:
        version = get_json(f"/api/v1/forms/{quote(args.form, safe='')}")["version"]

    if args.latest_version is not None:
        latest = get_json(f"/api/v1/forms/{quote(args.form, safe='')}")["version"]
        require(latest == args.latest_version and latest > version,
                "The server must have a newer published version than the captured draft")

    layer = f"/rest/services/{quote(args.service, safe='')}/FeatureServer/{args.layer}"
    escaped_name = args.name.replace("'", "''")
    query = get_json(layer + "/query", {
        "f": "json", "where": f"name = '{escaped_name}'", "outFields": "*",
        "returnGeometry": "true", "outSR": "4326",
    })
    features = query.get("features", [])
    require(len(features) == 1 and not query.get("exceededTransferLimit"),
            f"Expected exactly one stored feature; got {len(features)}")
    feature = features[0]
    attrs = {key.lower(): value for key, value in feature["attributes"].items()}
    object_id = int(attrs["objectid"])
    require(attrs["name"] == args.name, "Unexpected inspection name")
    require(sorted(json.loads(attrs["status"])) == sorted(args.status),
            "Unexpected multi-select values")
    require(attrs["total"] == args.total, "Unexpected calculated total")
    require(json.loads(attrs["visits"]) == [{"note": note} for note in args.repeat_note],
            "Unexpected repeat rows")
    geometry = feature["geometry"]
    require(math.isclose(geometry["x"], args.longitude, abs_tol=0.00001) and
            math.isclose(geometry["y"], args.latitude, abs_tol=0.00001),
            "Stored geometry differs from the captured WGS84 point")

    attachment_path = f"{layer}/{object_id}/attachments"
    attachments = get_json(attachment_path).get("attachmentInfos", [])
    require(len(attachments) == 1, f"Expected exactly one attachment; got {len(attachments)}")
    attachment_id = int(attachments[0]["id"])
    attachment = get(f"{attachment_path}/{attachment_id}")
    expected_hash = hashlib.sha256(args.attachment.read_bytes()).digest()
    require(attachment and hashlib.sha256(attachment).digest() == expected_hash,
            "Stored attachment bytes differ from the captured file")

    # The public FeatureServer response carries the object ID, while the
    # accepted receipt links that object ID to the exact submitted form version.
    receipts = subprocess.run([
        "docker", "exec", "-i", args.db_container, "psql", "-X", "-qAt",
        "-v", "ON_ERROR_STOP=1", "-v", f"service={args.service}", "-v", f"layer={args.layer}",
        "-v", f"object_id={object_id}", "-U", args.db_user, "-d", args.db_name,
    ], input="""
        SELECT json_build_array(form_id, form_version)
        FROM honua.form_submissions
        WHERE service_id = :'service' AND layer_id = :'layer'
          AND target_feature_id = :'object_id' AND status = 'accepted';
    """, check=True, text=True, stdout=subprocess.PIPE).stdout.splitlines()
    require([json.loads(row) for row in receipts] == [[args.form, version]],
            "Expected one accepted receipt for the captured form and version")
    if args.report:
        evidence = {
            "formId": args.form, "formVersion": version, "featureId": object_id,
            "attachmentId": attachment_id, "attachmentSha256": expected_hash.hex(),
            "matchedFeatures": len(features), "acceptedReceipts": len(receipts),
            "repeatRows": len(args.repeat_note),
        }
        if args.latest_version is not None:
            evidence["latestFormVersion"] = args.latest_version
        args.report.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    print(f"Verified feature {object_id}, attachment {attachment_id}, form version {version}")


if __name__ == "__main__":
    main()
