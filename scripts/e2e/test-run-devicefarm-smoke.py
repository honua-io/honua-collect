#!/usr/bin/env python3
"""Check the same-session install contract without reserving hardware."""

import importlib.util
import json
import plistlib
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, call, patch
from zipfile import ZipFile

spec = importlib.util.spec_from_file_location(
    "devicefarm_smoke", Path(__file__).with_name("run-devicefarm-smoke.py")
)
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


def ipa(path, build, platform="iPhoneOS"):
    with ZipFile(path, "w") as archive:
        archive.writestr("Payload/Field.app/Info.plist", plistlib.dumps({
            "CFBundleIdentifier": "io.honua.collect",
            "CFBundleSupportedPlatforms": [platform],
            "CFBundleVersion": str(build),
        }))
    return path


class PhysicalUpgradeTests(unittest.TestCase):
    def test_reject_simulator_ipa(self):
        with tempfile.TemporaryDirectory() as temp:
            artifact = ipa(Path(temp) / "field.ipa", 4, "iPhoneSimulator")
            with self.assertRaisesRegex(ValueError, "physical iPhone"):
                smoke.read_device_build(artifact)

    def test_installs_candidate_into_the_baseline_session(self):
        self.run_install(install_error=None)

    def test_cleans_up_after_candidate_install_error(self):
        self.run_install(install_error=RuntimeError("candidate rejected"))

    def run_install(self, install_error):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            args = SimpleNamespace(
                app=ipa(root / "old.ipa", 4),
                upgrade_app=ipa(root / "next.ipa", 5),
                revision="baseline",
                upgrade_revision="candidate",
                project_arn="project",
                device_arn="device",
                output=root / "evidence",
            )
            harness = Mock()
            farm, driver = harness.farm, harness.driver
            driver.page_source = "<App/>"
            farm.get_device.return_value = {"device": {
                "name": "Apple iPhone",
                "platform": "IOS",
                "os": "27.0",
            }}
            farm.create_remote_access_session.return_value = {
                "remoteAccessSession": {"arn": "session"},
            }
            farm.get_remote_access_session.return_value = {"remoteAccessSession": {
                "status": "RUNNING",
                "endpoints": {"remoteDriverEndpoint": "https://example.invalid"},
            }}
            farm.install_to_remote_access_session.side_effect = install_error
            harness.upload.side_effect = [("baseline-app", "baseline-hash"), ("candidate-app", "candidate-hash")]
            with (
                patch.object(smoke.boto3, "client", return_value=farm),
                patch.object(smoke.webdriver, "Remote", return_value=driver) as remote,
                patch.object(smoke, "upload_app", harness.upload),
                patch.object(smoke, "check_ios_build", harness.check),
                patch("builtins.print"),
            ):
                result = smoke.run(args)
            self.assertTrue(remote.call_args.kwargs["options"].capabilities["appium:noReset"])
            expected = [
                call.farm.get_device(arn="device"),
                call.upload(farm, args.app, "project", android=False),
                call.upload(farm, args.upgrade_app, "project", android=False),
                call.farm.create_remote_access_session(
                    projectArn="project", deviceArn="device", appArn="baseline-app", name="Field native smoke",
                ),
                call.farm.get_remote_access_session(arn="session"),
                call.check(driver, 4, args.output / "baseline.png"),
                call.driver.terminate_app("io.honua.collect"),
                call.farm.install_to_remote_access_session(remoteAccessSessionArn="session", appArn="candidate-app"),
            ]
            if install_error:
                expected.append(call.driver.save_screenshot(str(args.output / "failure.png")))
            else:
                expected.append(call.check(driver, 5, args.output / "candidate.png"))
            expected.extend([call.driver.quit(), call.farm.stop_remote_access_session(arn="session")])
            self.assertEqual(harness.mock_calls, expected)
            report = json.loads((args.output / "result.json").read_text())
            self.assertEqual(report["sessionArn"], "session")
            self.assertEqual(report["installedBaselineBuild"], 4)
            self.assertEqual(report["upgradeSha256"], "candidate-hash")
            if install_error:
                self.assertEqual(result, 1)
                self.assertEqual(report["failure"], "candidate rejected")
                self.assertNotIn("installedCandidateBuild", report)
            else:
                self.assertEqual(result, 0)
                self.assertEqual(report["installedCandidateBuild"], 5)


if __name__ == "__main__":
    unittest.main()
