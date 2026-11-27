#!/usr/bin/env python3
"""Run the native Records and geometry smoke on an AWS Device Farm device."""
import argparse
import hashlib
import json
import plistlib
import re
import time
from contextlib import suppress
from datetime import UTC, datetime
from pathlib import Path
from zipfile import ZipFile

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


def smoke(driver, output, *, android):
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
    geometry_tab = label("Geometry")
    if android:
        geometry_tab.click()
    else:
        # XCUITest can acknowledge an element click while the Records tab stays selected.
        rect = geometry_tab.rect
        driver.execute_script("mobile: tap", {"x": int(rect["x"] + rect["width"] / 2),
                                             "y": int(rect["y"] + rect["height"] / 2)})
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


def upload_app(farm, path, project, *, android):
    with path.open("rb") as app:
        digest = hashlib.file_digest(app, "sha256").hexdigest()
        app.seek(0)
        upload = farm.create_upload(projectArn=project, name=path.name,
                                    type="ANDROID_APP" if android else "IOS_APP")["upload"]
        response = requests.put(upload["url"], data=app, timeout=(15, 150))
        if not response.ok:
            raise RuntimeError(f"App upload returned HTTP {response.status_code}")
    ready(lambda: farm.get_upload(arn=upload["arn"])["upload"], "SUCCEEDED", 240)
    return upload["arn"], digest


def read_device_build(path):
    with ZipFile(path) as archive:
        (plist,) = (name for name in archive.namelist()
                    if re.fullmatch(r"Payload/[^/]+\.app/Info\.plist", name))
        info = plistlib.loads(archive.read(plist))
    if info["CFBundleSupportedPlatforms"] != ["iPhoneOS"] or info["CFBundleIdentifier"] != "io.honua.collect":
        raise ValueError("Expected a physical iPhone IPA for io.honua.collect")
    return int(info["CFBundleVersion"])


def check_ios_build(driver, build, screenshot):
    bundle = "io.honua.collect"

    def installed(browser):
        apps = browser.execute_script("mobile: listApps", {"returnAttributes": ["CFBundleVersion"]})
        return str(apps.get(bundle, {}).get("CFBundleVersion", "")) == str(build)

    WebDriverWait(driver, 90, 2).until(installed, f"Expected installed Field build {build}")
    driver.execute_script("mobile: launchApp", {"bundleId": bundle})
    WebDriverWait(driver, 35, 1).until(
        lambda browser: browser.query_app_state(bundle) == 4, f"Field build {build} did not reach the foreground")
    driver.save_screenshot(str(screenshot))


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
        upgrade = args.upgrade_app is not None
        if upgrade:
            if android or "iPhone" not in device["name"] or "Simulator" in device["name"]:
                raise ValueError("The install upgrade check requires a physical iPhone")
            baseline_build = read_device_build(args.app)
            candidate_build = read_device_build(args.upgrade_app)
            if candidate_build <= baseline_build:
                raise ValueError("The upgrade IPA must have a higher build number")
            report["scope"] = "same-session-install"
            report["upgradeRevision"] = args.upgrade_revision
        app_arn, report["sha256"] = upload_app(farm, args.app, args.project_arn, android=android)
        if upgrade:
            candidate_arn, report["upgradeSha256"] = upload_app(farm, args.upgrade_app, args.project_arn, android=False)
        session = farm.create_remote_access_session(projectArn=args.project_arn, deviceArn=args.device_arn,
                                                    appArn=app_arn, name="Field native smoke")["remoteAccessSession"]["arn"]
        report["sessionArn"] = session
        running = ready(lambda: farm.get_remote_access_session(arn=session)["remoteAccessSession"], "RUNNING", 480)
        endpoint = running["endpoints"]["remoteDriverEndpoint"]
        options = UiAutomator2Options() if android else XCUITestOptions()
        if android:
            options.auto_grant_permissions = True
        if upgrade:
            options.no_reset = True
        driver = webdriver.Remote(options=options, client_config=AppiumClientConfig(remote_server_addr=endpoint, timeout=120))
        if upgrade:
            check_ios_build(driver, baseline_build, args.output / "baseline.png")
            report["installedBaselineBuild"] = baseline_build
            driver.terminate_app("io.honua.collect")
            farm.install_to_remote_access_session(remoteAccessSessionArn=session, appArn=candidate_arn)
            check_ios_build(driver, candidate_build, args.output / "candidate.png")
            report["installedCandidateBuild"] = candidate_build
        else:
            smoke(driver, args.output, android=android)
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
    parser.add_argument("--upgrade-app", type=Path, help="Candidate IPA for same-session installation on physical iPhone")
    parser.add_argument("--upgrade-revision", help="Source revision for the candidate IPA")
    args = parser.parse_args()
    if bool(args.upgrade_app) != bool(args.upgrade_revision):
        parser.error("--upgrade-app and --upgrade-revision must be supplied together")
    raise SystemExit(run(args))
