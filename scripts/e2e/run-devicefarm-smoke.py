#!/usr/bin/env python3
"""Run native Field smoke tests on an AWS Device Farm device."""
import argparse
import hashlib
import json
import re
import time
from contextlib import suppress
from datetime import UTC, datetime
from pathlib import Path
from xml.etree import ElementTree

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


def smoke(driver, output, *, android, outbox_evidence=None):
    def visible(predicate, description):
        def find(browser):
            return next((e for e in browser.find_elements(AppiumBy.XPATH, f"//*[{predicate}]")
                         if e.is_displayed()), False)
        return WebDriverWait(driver, 35, 1).until(find, description)

    def label(text):
        return visible(" or ".join(f"@{attr}='{text}'" for attr in ("text", "label", "name", "content-desc"))
                       + f" or starts-with(@content-desc, '{text},')", text)

    def tap_tab(name):
        tab = label(name)
        if android:
            tab.click()
        else:
            # XCUITest can acknowledge an element click without changing tabs.
            rect = tab.rect
            driver.execute_script("mobile: tap", {"x": int(rect["x"] + rect["width"] / 2),
                                                 "y": int(rect["y"] + rect["height"] / 2)})

    tap_tab("Records")
    for expected in ("No drafts.", "Outbox is empty.", "Nothing sent yet."):
        label(expected)
    driver.save_screenshot(str(output / "records.png"))
    tap_tab("Geometry")
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

    if outbox_evidence is None:
        return

    def queued_id():
        label("Drafts 0 · Outbox 1 · Conflicts 0 · Sent 0")

        def ids(browser):
            tree = ElementTree.fromstring(browser.page_source)
            identifiers = set()
            for row in tree.iter():
                if row.get("visible") != "true":
                    continue
                for attribute in ("label", "name", "value"):
                    value = re.sub(r"(?<=[0-9a-f])\s+(?=[0-9a-f])", "", row.get(attribute, ""))
                    identifiers.update(re.findall(r"(?<![0-9a-f])[0-9a-f]{32}(?![0-9a-f])", value))
            return identifiers

        found = WebDriverWait(driver, 35, 1).until(ids, "Visible Outbox record ID")
        if len(found) != 1:
            raise ValueError("Expected exactly one visible Outbox record ID")
        return next(iter(found))

    outbox_evidence["phase"] = "open-inspection"
    tap_tab("Capture")
    label("Sample Inspection Demo").click()
    outbox_evidence["phase"] = "fill-inspection"
    site = visible("@name='site_name'", "Site name")
    site.click()
    site.send_keys("Device Farm inspection")
    label("Done").click()
    status = label("Status").rect
    required = label("Status is required.").rect
    driver.tap([(driver.get_window_size()["width"] // 2, (status["y"] + status["height"] + required["y"]) // 2)])
    wheel = visible("@type='XCUIElementTypePickerWheel'", "Status picker wheel")
    driver.execute_script("mobile: selectPickerWheelValue", {"elementId": wheel.id, "order": "next"})
    label("Done").click()
    WebDriverWait(driver, 10).until(lambda app: not any(
        element.is_displayed() for element in app.find_elements(
            AppiumBy.IOS_PREDICATE, "label == 'Status is required.'")))

    outbox_evidence["phase"] = "submit-inspection"
    label("Submit").click()
    visible(
        "@type='XCUIElementTypeButton' and (@name='Honua Field' or @label='Honua Field')",
        "Back to Honua Field",
    ).click()
    visible(
        "contains(@label,'kept in Outbox') or starts-with(@label,'Sync error:')",
        "Offline inspection submission status",
    )
    tap_tab("Records")
    outbox_evidence["beforeRestart"] = queued_id()
    driver.save_screenshot(str(output / "outbox-before-restart.png"))

    outbox_evidence["phase"] = "restart"
    app_id = "io.honua.collect"
    driver.terminate_app(app_id)
    wait = WebDriverWait(driver, 35, 1)
    wait.until(lambda app: app.query_app_state(app_id) == 1, "Field process stopped")
    driver.activate_app(app_id)
    wait.until(lambda app: app.query_app_state(app_id) == 4, "Field foregrounded")
    tap_tab("Records")
    outbox_evidence["afterRestart"] = queued_id()
    if outbox_evidence["beforeRestart"] != outbox_evidence["afterRestart"]:
        raise ValueError("The queued inspection ID changed after restart")
    driver.save_screenshot(str(output / "outbox-after-restart.png"))
    outbox_evidence["phase"] = "verified"


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
        if android and args.bundled_inspection_outbox:
            raise ValueError("Bundled inspection Outbox smoke requires iOS")
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
        outbox_evidence = report.setdefault("bundledInspectionOutbox", {}) if args.bundled_inspection_outbox else None
        smoke(driver, args.output, android=android, outbox_evidence=outbox_evidence)
        report["outcome"] = "passed"
    except Exception as error:
        report["failure"] = re.sub(r"https?://\S+|/remote-endpoint/\S+", "[endpoint]", str(error).split("Stacktrace:")[0])[:1200]
        if driver:
            with suppress(Exception):
                driver.save_screenshot(str(args.output / "failure.png"))
                if not args.bundled_inspection_outbox:
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
    parser.add_argument("--bundled-inspection-outbox", action="store_true",
                        help="Verify the bundled iOS inspection survives process restart")
    raise SystemExit(run(parser.parse_args()))
