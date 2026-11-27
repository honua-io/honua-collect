#!/usr/bin/env python3
"""Run the native Records and geometry smoke on an AWS Device Farm device."""
import argparse
import hashlib
import json
import re
import time
from contextlib import suppress
from datetime import UTC, datetime
from pathlib import Path

import boto3
import requests
from appium import webdriver
from appium.options.android import UiAutomator2Options
from appium.options.ios import XCUITestOptions
from appium.webdriver.client_config import AppiumClientConfig
from appium.webdriver.common.appiumby import AppiumBy
from botocore.config import Config
from selenium.webdriver.common.actions.action_builder import ActionBuilder
from selenium.webdriver.common.actions.pointer_input import PointerInput
from selenium.webdriver.support.ui import WebDriverWait


def ready(read, expected, seconds):
    deadline = time.monotonic() + seconds
    last = None
    while time.monotonic() < deadline:
        item = read()
        status = item["status"]
        if status != last:
            print(status, flush=True)
            last = status
        if status == expected:
            return item
        if status in {"FAILED", "ERRORED", "STOPPING", "COMPLETED"}:
            raise RuntimeError(f"Device Farm returned {status} before {expected}: {item.get('message', '')}")
        time.sleep(5)
    raise TimeoutError(f"Timed out waiting for {expected}, last status {last}")


def smoke(driver, output):
    def visible(predicate, description):
        def find(browser):
            return next((e for e in browser.find_elements(AppiumBy.XPATH, f"//*[{predicate}]")
                         if e.is_displayed()), False)
        return WebDriverWait(driver, 35, 1).until(find, description)

    def label(text):
        return visible(" or ".join(f"@{attr}='{text}'" for attr in ("text", "label", "name", "content-desc"))
                       + f" or starts-with(@content-desc, '{text},')", text)

    label("Records").click()
    for expected in ("No drafts.", "Outbox is empty.", "Nothing sent yet."):
        label(expected)
    driver.save_screenshot(str(output / "records.png"))
    label("Geometry").click()
    label("Point: 0 vertex(es)")
    canvas = visible("@name='GeometryCanvas' or contains(@resource-id,'GeometryCanvas') or "
                     "@label='Geometry drawing area' or @content-desc='Geometry drawing area'", "drawing canvas")
    rect = canvas.rect
    touch = ActionBuilder(driver, mouse=PointerInput("touch", "finger"))
    touch.pointer_action.move_to_location(int(rect["x"] + rect["width"] / 2), int(rect["y"] + rect["height"] / 2))
    touch.pointer_action.pointer_down()
    touch.pointer_action.pointer_up()
    touch.perform()
    label("Point: 1 vertex(es) ✓")
    label("Done").click()
    label("Geometry captured")
    message = visible(" or ".join(f"(contains(@{attr},'coordinates') and contains(@{attr},'Point'))"
                                  for attr in ("text", "label", "name")), "Point GeoJSON")
    geojson = json.loads(message.text or message.get_attribute("label"))
    if geojson["type"] != "Point" or len(geojson["coordinates"]) != 2:
        raise ValueError("Expected a two-dimensional GeoJSON point")
    driver.save_screenshot(str(output / "point.png"))
    label("OK").click()
    label("Undo").click()
    label("Point: 0 vertex(es)")


def run(args):
    args.output.mkdir(parents=True, exist_ok=False)
    farm = boto3.client("devicefarm", region_name="us-west-2",
                        config=Config(connect_timeout=10, read_timeout=40, retries={"total_max_attempts": 3}))
    report = {"deviceArn": args.device_arn, "revision": args.revision, "outcome": "failed",
              "startedAt": datetime.now(UTC).isoformat()}
    session = driver = None
    try:
        device = farm.get_device(arn=args.device_arn)["device"]
        android = device["platform"] == "ANDROID"
        report.update(device=device["name"], os=device["os"], platform=device["platform"])
        with args.app.open("rb") as app:
            report["sha256"] = hashlib.file_digest(app, "sha256").hexdigest()
            app.seek(0)
            upload = farm.create_upload(projectArn=args.project_arn, name=args.app.name,
                                        type="ANDROID_APP" if android else "IOS_APP")["upload"]
            response = requests.put(upload["url"], data=app, timeout=(15, 150))
            if not response.ok:
                raise RuntimeError(f"App upload returned HTTP {response.status_code}")
        ready(lambda: farm.get_upload(arn=upload["arn"])["upload"], "SUCCEEDED", 240)
        session = farm.create_remote_access_session(projectArn=args.project_arn, deviceArn=args.device_arn,
                                                    appArn=upload["arn"], name="Field native smoke")["remoteAccessSession"]["arn"]
        report["sessionArn"] = session
        running = ready(lambda: farm.get_remote_access_session(arn=session)["remoteAccessSession"], "RUNNING", 480)
        endpoint = running["endpoints"]["remoteDriverEndpoint"]
        options = UiAutomator2Options() if android else XCUITestOptions()
        if android:
            options.auto_grant_permissions = True
        driver = webdriver.Remote(options=options, client_config=AppiumClientConfig(remote_server_addr=endpoint, timeout=120))
        smoke(driver, args.output)
        report["outcome"] = "passed"
    except Exception as error:
        report["failure"] = re.sub(r"https?://\S+|/remote-endpoint/\S+", "[endpoint]", str(error).split("Stacktrace:")[0])[:1200]
        if driver:
            with suppress(Exception):
                driver.save_screenshot(str(args.output / "failure.png"))
                (args.output / "failure.xml").write_text(driver.page_source)
    finally:
        try:
            if driver:
                with suppress(Exception):
                    driver.quit()
            if session:
                farm.stop_remote_access_session(arn=session)
        finally:
            report["finishedAt"] = datetime.now(UTC).isoformat()
            (args.output / "result.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))
    return 0 if report["outcome"] == "passed" else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-arn", required=True)
    parser.add_argument("--device-arn", required=True)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--output", type=Path, required=True, help="New evidence directory")
    raise SystemExit(run(parser.parse_args()))
