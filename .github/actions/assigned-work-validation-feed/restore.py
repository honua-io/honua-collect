"""Verify the exact recorded Actions archives before exposing them to NuGet."""
import hashlib
from io import BytesIO
import json
import os
from pathlib import Path
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
    if run["head_sha"] != candidate["revision"] or run["conclusion"] != "success":
        raise SystemExit(f"Unexpected validation run for {candidate['source']}")
    artifact = f"{endpoint}/artifacts/{candidate['id']}"
    metadata = json.loads(api(artifact))
    if metadata["workflow_run"]["id"] != candidate["run"] or metadata["digest"] != f"sha256:{candidate['sha256']}":
        raise SystemExit(f"Unexpected validation artifact metadata for {candidate['source']}")

    feed = Path(mkdtemp(dir=os.environ["RUNNER_TEMP"], prefix="honua-task-feed."))
    archive = api(f"{artifact}/zip")
    if hashlib.sha256(archive).hexdigest() != candidate["sha256"]:
        raise SystemExit(f"Archive checksum mismatch for {candidate['source']}")
    with ZipFile(BytesIO(archive)) as packages:
        for package_id in candidate["packages"]:
            filename = f"{package_id}.{candidate['version']}.nupkg"
            (feed / filename).write_bytes(packages.read(filename))

    ET.SubElement(sources, "add", key=candidate["source"], value=str(feed))
    mapping = ET.SubElement(source_mapping, "packageSource", key=candidate["source"])
    for package_id in candidate["packages"]:
        ET.SubElement(mapping, "package", pattern=package_id)

ET.indent(tree)
tree.write(config, encoding="utf-8", xml_declaration=True)
