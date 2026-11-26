#!/usr/bin/env python3
# Copyright (c) Honua Geo Inc.
# SPDX-License-Identifier: Apache-2.0

"""Check source and optionally built artifacts against the installed app identity."""

import argparse
from pathlib import Path
import plistlib
import re
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
APP = ROOT / "src/Honua.Field.App"
APP_ID = "io.honua.collect"
APP_TITLE = "Honua Field"
ANDROID_LAUNCHER = "crc6490b85cd101108deb.MainActivity"
LEGACY_VERSION = 1


def require(condition, message):
    if not condition:
        raise SystemExit(message)


def badging(aapt2, apk):
    return subprocess.check_output([aapt2, "dump", "badging", apk], text=True)


def signing_certificates(apksigner, apk):
    result = subprocess.check_output(
        [apksigner, "verify", "--print-certs", apk], text=True
    )
    certificates = set(re.findall(r"^Signer #\d+ certificate SHA-256 digest: (\w+)$", result, re.M))
    require(certificates, f"No signing certificates found in {apk}")
    return certificates


def check_source():
    properties = ET.parse(APP / "Honua.Field.App.csproj")
    require(properties.findtext("./PropertyGroup/ApplicationId") == APP_ID, "App ID changed")
    require(properties.findtext("./PropertyGroup/ApplicationTitle") == APP_TITLE, "Unexpected app title")
    require(int(properties.findtext("./PropertyGroup/ApplicationVersion")) > LEGACY_VERSION, "Upgrade version must increase")

    stable_values = {
        APP / "Platforms/Android/MainActivity.cs": [f'Name = "{ANDROID_LAUNCHER}"'],
        APP / "Platforms/Windows/app.manifest": ['name="Honua.Collect.App.WinUI.app"'],
        APP / "MauiProgram.cs": ['"collect-records.db"', '"collect-audit.db"'],
        APP / "Capture/CaptureFiles.cs": ['"media"'],
        APP / "Services/DbKeyProvider.cs": ['"collect_db_key"'],
        APP / "Services/SecureStorageSessionPersistence.cs": ['"collect_auth_session"'],
        APP / "Services/PreferencesInboxPullProgressStore.cs": ['"honua_collect_inbox_pull:'],
        APP / "Services/AppSettings.cs": ['"appsettings.local.json"'],
        APP / "Views/FormPage.xaml.cs": ['"anthropic_api_key"'],
        APP / "Views/GeometryCapturePage.xaml.cs": ['"tiles"'],
        ROOT / "src/Honua.Field.Core/Storage/SqliteRecordStore.cs": ['"collect_records"'],
    }
    for path, values in stable_values.items():
        source = path.read_text(encoding="utf-8-sig")
        for value in values:
            require(value in source, f"Upgrade value {value} missing from {path.relative_to(ROOT)}")


def check_android(args):
    result = badging(args.aapt2, args.apk)
    package = re.search(r"^package: name='([^']+)' versionCode='(\d+)'", result, re.M)
    require(package and package[1] == APP_ID, "Built Android application ID changed")
    require(f"application-label:'{APP_TITLE}'" in result, "Built Android launcher title changed")
    require(f"launchable-activity: name='{ANDROID_LAUNCHER}'" in result, "Built Android launcher component changed")
    prior_version = LEGACY_VERSION
    if args.previous_apk:
        prior = re.search(
            r"^package: name='([^']+)' versionCode='(\d+)'",
            badging(args.aapt2, args.previous_apk),
            re.M,
        )
        require(prior and prior[1] == APP_ID, "Previous APK has a different application ID")
        prior_version = int(prior[2])
        require(
            signing_certificates(args.apksigner, args.apk)
            == signing_certificates(args.apksigner, args.previous_apk),
            "APK signing certificates differ",
        )
    require(int(package[2]) > prior_version, "Built Android upgrade version must increase")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apk")
    parser.add_argument("--aapt2", default="aapt2")
    parser.add_argument("--previous-apk")
    parser.add_argument("--apksigner", default="apksigner")
    parser.add_argument("--apple-info-plist", type=Path)
    args = parser.parse_args()
    if args.previous_apk and not args.apk:
        parser.error("--previous-apk requires --apk")

    check_source()
    if args.apk:
        check_android(args)
    if args.apple_info_plist:
        with args.apple_info_plist.open("rb") as stream:
            plist = plistlib.load(stream)
        require(plist.get("CFBundleIdentifier") == APP_ID, "Built Apple bundle ID changed")
        require(plist.get("CFBundleDisplayName") == APP_TITLE, "Built Apple app title changed")
        require(int(plist["CFBundleVersion"].split(".")[0]) > LEGACY_VERSION, "Built Apple upgrade version must increase")
    print("Honua Field upgrade identity checks passed.")


if __name__ == "__main__":
    main()
