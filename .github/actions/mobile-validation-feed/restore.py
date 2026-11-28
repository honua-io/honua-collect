"""Verify the exact recorded Actions archives before exposing them to NuGet."""
import hashlib
from io import BytesIO
import json
import os
from pathlib import Path, PurePosixPath
import subprocess
from tempfile import mkdtemp
import xml.etree.ElementTree as ET
from zipfile import ZipFile


def api(path):
    return subprocess.check_output(["gh", "api", path])


here = Path(__file__).parent
config = Path("NuGet.config")
tree = ET.parse(config)
sources = tree.find("packageSources")
source_mapping = tree.find("packageSourceMapping")

for candidate in json.loads((here / "artifacts.json").read_text()):
    endpoint = f"repos/{candidate['repo']}/actions"
    run = json.loads(api(f"{endpoint}/runs/{candidate['run']}"))
    if (
        run["repository"]["id"] != candidate["repository_id"]
        or run["path"] != ".github/workflows/publish-dotnet-mobile.yml"
        or run["event"] != "push"
        or run["head_branch"] != candidate["tag"]
        or run["head_sha"] != candidate["revision"]
        or run["status"] != "completed"
    ):
        raise SystemExit("Unexpected tagged Mobile publication run")

    repository = f"repos/{candidate['repo']}"
    ref = json.loads(api(f"{repository}/git/ref/tags/{candidate['tag']}"))
    tag = json.loads(api(f"{repository}/git/tags/{candidate['tag_object']}"))
    if (
        ref["object"]["type"] != "tag"
        or ref["object"]["sha"] != candidate["tag_object"]
        or tag["tag"] != candidate["tag"]
        or tag["object"]["type"] != "commit"
        or tag["object"]["sha"] != candidate["revision"]
    ):
        raise SystemExit("Unexpected annotated Mobile tag or target")

    job = json.loads(api(f"{endpoint}/jobs/{candidate['job']}"))
    successful_steps = {step["name"] for step in job["steps"] if step["conclusion"] == "success"}
    required_steps = {
        "Inspect release tag and bind it to the checkout",
        "Verify signed release tag",
        "Checksum the package set",
        "Verify the exact four public package artifacts",
        "Upload package artifacts",
        "Attest NuGet package provenance",
    }
    if (
        job["run_id"] != candidate["run"]
        or job["head_sha"] != candidate["revision"]
        or job["name"] != "Package Smoke"
        or job["conclusion"] != "success"
        or not required_steps <= successful_steps
    ):
        raise SystemExit("Mobile Package Smoke did not verify and produce these packages")
    artifact = f"{endpoint}/artifacts/{candidate['id']}"
    metadata = json.loads(api(artifact))
    workflow = metadata["workflow_run"]
    if (
        metadata["id"] != candidate["id"]
        or metadata["name"] != f"honua-mobile-dotnet-{candidate['run']}-packages"
        or metadata["expired"]
        or metadata["digest"] != f"sha256:{candidate['sha256']}"
        or workflow["id"] != candidate["run"]
        or workflow["repository_id"] != candidate["repository_id"]
        or workflow["head_repository_id"] != candidate["repository_id"]
        or workflow["head_branch"] != candidate["tag"]
        or workflow["head_sha"] != candidate["revision"]
    ):
        raise SystemExit(f"Unexpected validation artifact metadata for {candidate['source']}")

    feed = Path(mkdtemp(dir=os.environ["RUNNER_TEMP"], prefix="honua-validation-feed."))
    archive = api(f"{artifact}/zip")
    if hashlib.sha256(archive).hexdigest() != candidate["sha256"]:
        raise SystemExit(f"Archive checksum mismatch for {candidate['source']}")
    with ZipFile(BytesIO(archive)) as packages:
        if len(packages.namelist()) != len(candidate["packages"]):
            raise SystemExit("Unexpected files in the Mobile package artifact")
        for package_id, digest in candidate["packages"].items():
            filename = f"{package_id}.{candidate['version']}.nupkg"
            matches = [name for name in packages.namelist() if PurePosixPath(name).name == filename]
            if len(matches) != 1:
                raise SystemExit(f"Expected exactly one copy of {filename}")
            payload = packages.read(matches[0])
            if hashlib.sha256(payload).hexdigest() != digest:
                raise SystemExit(f"Package checksum mismatch for {filename}")
            (feed / filename).write_bytes(payload)
            print(f"{digest}  {filename}")

    print(f"Verified artifact {candidate['id']} from {candidate['tag']} at {candidate['revision']}")

    ET.SubElement(sources, "add", key=candidate["source"], value=str(feed))
    mapping = ET.SubElement(source_mapping, "packageSource", key=candidate["source"])
    for package_id in candidate["packages"]:
        ET.SubElement(mapping, "package", pattern=package_id)

ET.indent(tree)
tree.write(config, encoding="utf-8", xml_declaration=True)
