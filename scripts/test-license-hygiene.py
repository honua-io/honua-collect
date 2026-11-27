#!/usr/bin/env python3
"""Unit tests for scripts/check-license-hygiene.py."""
from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path

SCRIPT_PATH = Path(__file__).resolve().parent / "check-license-hygiene.py"
SPEC = importlib.util.spec_from_file_location("check_license_hygiene", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
MOD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MOD)


def write_fixture_repo(root: Path) -> None:
    (root / "src/Honua.Field.App").mkdir(parents=True)
    (root / "tests/Honua.Field.Core.Tests").mkdir(parents=True)
    (root / "Directory.Packages.props").write_text(
        "<Project>\n"
        "  <PropertyGroup><SdkVer>1.2.3</SdkVer></PropertyGroup>\n"
        "  <ItemGroup>\n"
        '    <PackageVersion Include="Honua.Sdk.Field" Version="$(SdkVer)" />\n'
        '    <PackageVersion Include="SkiaSharp" Version="4.154.0" />\n'
        '    <PackageVersion Include="xunit" Version="2.9.3" />\n'
        "  </ItemGroup>\n"
        "</Project>\n",
        encoding="utf-8",
    )
    (root / "docs").mkdir(parents=True)
    (root / "src/Honua.Field.App/Honua.Field.App.csproj").write_text(
        "<Project>\n"
        "  <ItemGroup>\n"
        '    <MauiAsset Include="..\\..\\docs\\THIRD-PARTY-NOTICES.md" LogicalName="THIRD-PARTY-NOTICES.md" />\n'
        '    <MauiAsset Include="..\\..\\NOTICE" LogicalName="NOTICE" />\n'
        "  </ItemGroup>\n"
        "  <ItemGroup>\n"
        '    <PackageReference Include="Honua.Sdk.Field" />\n'
        '    <PackageReference Include="SkiaSharp" />\n'
        "  </ItemGroup>\n"
        "</Project>\n",
        encoding="utf-8",
    )
    (root / "docs/THIRD-PARTY-NOTICES.md").write_text(
        "# Notices\n\n"
        f"{MOD.TABLE_START}\n"
        "| Package | Version | License | Project |\n"
        "|---|---|---|---|\n"
        "| Honua.Sdk.Field | 1.2.3 | Apache-2.0 | https://example.test |\n"
        "| SkiaSharp | 4.154.0 | MIT | https://example.test |\n"
        f"{MOD.TABLE_END}\n",
        encoding="utf-8",
    )
    valid_cs = (
        "// Copyright (c) Honua Geo Inc.\n"
        "// SPDX-License-Identifier: Apache-2.0\n\n"
        "namespace Demo;\n"
    )
    (root / "src/Honua.Field.App/Program.cs").write_text(valid_cs, encoding="utf-8")
    (root / "tests/Honua.Field.Core.Tests/SampleTests.cs").write_text(valid_cs, encoding="utf-8")


class LicenseHygieneTests(unittest.TestCase):
    def test_valid_repo_passes(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_fixture_repo(root)
            self.assertEqual(MOD.run_checks(root), [])

    def test_missing_header_fails_and_write_preserves_bom_and_crlf(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_fixture_repo(root)
            target = root / "src/Honua.Field.App/Program.cs"
            target.write_bytes(b"\xef\xbb\xbfnamespace Demo;\r\npublic class A {}\r\n")

            errors = MOD.run_checks(root)
            self.assertEqual(len(errors), 1)
            self.assertIn("Missing Apache-2.0 SPDX header", errors[0])

            self.assertEqual(MOD.main(["--repo-root", str(root), "--write-headers"]), 0)
            raw = target.read_bytes()
            self.assertTrue(raw.startswith(b"\xef\xbb\xbf// Copyright (c) Honua Geo Inc.\r\n"))
            self.assertEqual(MOD.run_checks(root), [])

    def test_wrong_license_identifier_fails(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_fixture_repo(root)
            target = root / "tests/Honua.Field.Core.Tests/SampleTests.cs"
            target.write_text("// Copyright (c) Honua Geo Inc.\n// SPDX-License-Identifier: MIT\n", encoding="utf-8")
            errors = MOD.run_checks(root)
            self.assertEqual(len(errors), 1)

    def test_version_mismatch_and_missing_package_fail(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_fixture_repo(root)
            notices = root / "docs/THIRD-PARTY-NOTICES.md"
            notices.write_text(
                f"{MOD.TABLE_START}\n"
                "| Package | Version | License | Project |\n"
                "|---|---|---|---|\n"
                "| Honua.Sdk.Field | 9.9.9 | Apache-2.0 | https://example.test |\n"
                f"{MOD.TABLE_END}\n",
                encoding="utf-8",
            )
            errors = MOD.run_checks(root)
            self.assertTrue(any("Version mismatch" in e and "Honua.Sdk.Field" in e for e in errors))
            self.assertTrue(any("missing from docs/THIRD-PARTY-NOTICES.md" in e and "SkiaSharp" in e for e in errors))

    def test_package_assets_determine_shipping_notices(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            src = root / "src"
            src.mkdir()
            (src / "App.csproj").write_text(
                '<Project><ItemGroup>'
                '<PackageReference Include="Suppressed" ExcludeAssets="all" />'
                '<PackageReference Include="SuppressedChild"><ExcludeAssets>All</ExcludeAssets></PackageReference>'
                '<PackageReference Include="Partial" ExcludeAssets="analyzers" />'
                '<PackageReference Include="Conditional"><ExcludeAssets Condition="foo">all</ExcludeAssets></PackageReference>'
                '<PackageReference Include="Platform" ExcludeAssets="all" />'
                '<PackageReference Include="Platform" />'
                '</ItemGroup></Project>', encoding="utf-8")
            self.assertEqual(MOD.find_shipping_packages(root), {"Partial", "Conditional", "Platform"})

    def test_missing_app_maui_asset_fails(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            write_fixture_repo(root)
            csproj = root / "src/Honua.Field.App/Honua.Field.App.csproj"
            csproj.write_text(
                "<Project><ItemGroup>"
                '<PackageReference Include="Honua.Sdk.Field" />'
                '<PackageReference Include="SkiaSharp" />'
                "</ItemGroup></Project>\n",
                encoding="utf-8",
            )
            errors = MOD.run_checks(root)
            self.assertEqual(len(errors), 2)
            self.assertTrue(any("THIRD-PARTY-NOTICES.md" in e for e in errors))
            self.assertTrue(any("NOTICE" in e for e in errors))


if __name__ == "__main__":
    unittest.main()
