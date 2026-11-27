#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")"
umask 077
tar -tzf server.tar.gz | awk '$0=="published-server/Honua.Server" { found=1 } END {exit !found}'
python3 - <<'PYTHON'
import copy
from zipfile import ZipFile

with ZipFile("candidate.zip") as source, ZipFile("field-live.ipa", "w") as target:
    seen_application = False
    for member in source.infolist():
        if not member.filename.split("/", 1)[0].endswith(".app"):
            continue
        seen_application |= member.filename.count("/") == 1 and member.filename.endswith("/Info.plist")
        entry = copy.copy(member)
        entry.filename = "Payload/" + entry.filename
        target.writestr(entry, source.read(member))
    if not seen_application:
        raise SystemExit("candidate archive does not contain the simulator application")
PYTHON
sha256sum previous.zip candidate.zip server.tar.gz fixture.tar provenance.txt > inputs.sha256
python3 -m zipfile --create field-ios-honua-live.zip live.sh fixture.tar previous.zip candidate.zip server.tar.gz provenance.txt inputs.sha256 tests wheelhouse requirements.txt
