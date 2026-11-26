#!/usr/bin/env python3
"""Verify SPDX license headers and docs/THIRD-PARTY-NOTICES.md alignment.

Checks:
1. Every C# file under src/ and tests/ (excluding bin/ and obj/) begins with
   the repository Apache-2.0 SPDX header.
2. docs/THIRD-PARTY-NOTICES.md lists every shipping PackageReference from
   src/**/*.csproj with its exact resolved version from Directory.Packages.props.
3. src/Honua.Field.App/Honua.Field.App.csproj packages docs/THIRD-PARTY-NOTICES.md
   and NOTICE as MauiAsset entries so shipped binaries carry attributions.
"""
from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

EXPECTED_HEADER_LINES = (
    "// Copyright (c) Honua Geo Inc.",
    "// SPDX-License-Identifier: Apache-2.0",
)
TABLE_START = "<!-- nuget-table:start (verified by scripts/check-license-hygiene.py) -->"
TABLE_END = "<!-- nuget-table:end -->"
PROP_REF_RE = re.compile(r"\$\(([^)]+)\)")
SKIP_DIRS = {"bin", "obj"}


def iter_cs_files(repo_root: Path) -> list[Path]:
    files: list[Path] = []
    for root_name in ("src", "tests"):
        base = repo_root / root_name
        if not base.is_dir():
            continue
        for path in sorted(base.rglob("*.cs")):
            if not any(part in SKIP_DIRS for part in path.relative_to(repo_root).parts):
                files.append(path)
    return files


def has_valid_header(text: str) -> bool:
    lines = text.splitlines()
    return len(lines) >= 2 and lines[0] == EXPECTED_HEADER_LINES[0] and lines[1] == EXPECTED_HEADER_LINES[1]


def prepend_header(path: Path) -> bool:
    raw = path.read_bytes()
    has_bom = raw.startswith(b"\xef\xbb\xbf")
    body = raw[3:] if has_bom else raw
    text = body.decode("utf-8")
    if has_valid_header(text):
        return False
    newline = "\r\n" if "\r\n" in text else "\n"
    header = f"{EXPECTED_HEADER_LINES[0]}{newline}{EXPECTED_HEADER_LINES[1]}{newline}{newline}"
    updated = (b"\xef\xbb\xbf" if has_bom else b"") + (header + text).encode("utf-8")
    path.write_bytes(updated)
    return True


def check_spdx_headers(repo_root: Path) -> list[str]:
    errors: list[str] = []
    for path in iter_cs_files(repo_root):
        text = path.read_text("utf-8-sig")
        if not has_valid_header(text):
            rel = path.relative_to(repo_root).as_posix()
            errors.append(f"Missing Apache-2.0 SPDX header: {rel}")
    return errors


def parse_package_versions(props_path: Path) -> dict[str, str]:
    root = ET.fromstring(props_path.read_text("utf-8-sig"))
    props: dict[str, str] = {}
    for group in root.findall("PropertyGroup"):
        for elem in group:
            if elem.text is not None:
                props[elem.tag] = elem.text.strip()

    def expand(value: str) -> str:
        return PROP_REF_RE.sub(lambda m: props.get(m.group(1), m.group(0)), value)

    versions: dict[str, str] = {}
    for item in root.findall(".//PackageVersion"):
        include = item.attrib.get("Include")
        version = item.attrib.get("Version")
        if include and version:
            versions[include] = expand(version)
    return versions


def find_shipping_packages(repo_root: Path) -> set[str]:
    src_dir = repo_root / "src"
    packages: set[str] = set()
    if not src_dir.is_dir():
        return packages
    for csproj in sorted(src_dir.rglob("*.csproj")):
        root = ET.fromstring(csproj.read_text("utf-8-sig"))
        for ref in root.findall(".//PackageReference"):
            include = ref.attrib.get("Include")
            if include:
                packages.add(include)
    return packages


def parse_notices_table(notices_path: Path) -> dict[str, str]:
    text = notices_path.read_text("utf-8-sig")
    if TABLE_START not in text or TABLE_END not in text:
        raise ValueError("THIRD-PARTY-NOTICES.md is missing table markers")
    section = text.split(TABLE_START, 1)[1].split(TABLE_END, 1)[0]
    entries: dict[str, str] = {}
    for line in section.splitlines():
        cols = [col.strip() for col in line.strip().strip("|").split("|")]
        if len(cols) >= 2 and cols[0] and cols[0] != "Package" and not cols[0].startswith("---"):
            entries[cols[0]] = cols[1]
    return entries


def check_third_party_notices(repo_root: Path) -> list[str]:
    notices_path = repo_root / "docs/THIRD-PARTY-NOTICES.md"
    props_path = repo_root / "Directory.Packages.props"
    if not notices_path.is_file():
        return ["Missing docs/THIRD-PARTY-NOTICES.md"]
    if not props_path.is_file():
        return ["Missing Directory.Packages.props at repository root"]

    try:
        listed = parse_notices_table(notices_path)
    except ValueError as exc:
        return [str(exc)]

    pinned = parse_package_versions(props_path)
    shipping = find_shipping_packages(repo_root)
    errors: list[str] = []
    for pkg in sorted(shipping):
        expected_version = pinned.get(pkg)
        if expected_version is None:
            errors.append(f"Shipping package missing from Directory.Packages.props: {pkg}")
        elif pkg not in listed:
            errors.append(f"Shipping package missing from docs/THIRD-PARTY-NOTICES.md: {pkg} ({expected_version})")
        elif listed[pkg] != expected_version:
            errors.append(
                f"Version mismatch in docs/THIRD-PARTY-NOTICES.md for {pkg}: "
                f"found {listed[pkg]}, expected {expected_version}"
            )
    for extra in sorted(set(listed) - shipping):
        errors.append(f"Non-shipping package listed in docs/THIRD-PARTY-NOTICES.md: {extra}")
    return errors


def check_app_package_assets(repo_root: Path) -> list[str]:
    csproj = repo_root / "src/Honua.Field.App/Honua.Field.App.csproj"
    if not csproj.is_file():
        return ["Missing src/Honua.Field.App/Honua.Field.App.csproj"]
    root = ET.fromstring(csproj.read_text("utf-8-sig"))
    logical_names = {
        elem.attrib.get("LogicalName", "")
        for elem in root.findall(".//MauiAsset")
    }
    errors: list[str] = []
    for required in ("THIRD-PARTY-NOTICES.md", "NOTICE"):
        if required not in logical_names:
            errors.append(f"Honua.Field.App.csproj must include MauiAsset LogicalName='{required}'")
    return errors


def run_checks(repo_root: Path) -> list[str]:
    errors = check_spdx_headers(repo_root)
    errors.extend(check_third_party_notices(repo_root))
    errors.extend(check_app_package_assets(repo_root))
    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo-root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--write-headers", action="store_true", help="Prepend missing SPDX headers to .cs files")
    args = parser.parse_args(argv)
    repo_root = args.repo_root.resolve()

    if args.write_headers:
        updated = sum(1 for path in iter_cs_files(repo_root) if prepend_header(path))
        print(f"Updated SPDX headers in {updated} files.")
        return 0

    errors = run_checks(repo_root)
    if errors:
        for err in errors:
            print(f"ERROR: {err}", file=sys.stderr)
        print(f"License hygiene check failed with {len(errors)} issue(s).", file=sys.stderr)
        return 1
    cs_count = len(iter_cs_files(repo_root))
    pkg_count = len(find_shipping_packages(repo_root))
    print(f"License hygiene OK ({cs_count} C# files verified, {pkg_count} shipping packages aligned).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
