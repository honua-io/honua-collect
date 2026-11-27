#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")"
umask 077
baseline=6e3db0b0c48c63d1bdb6fd1d7f00c2030dc33c46
candidate=c6551c28995ed4f15cd9db92ec5df95938ae0da4
sha256sum --check app-digests.sha256
tar -tzf server.tar.gz | awk '$0=="published-server/Honua.Server" { found=1 } END {exit !found}'
python3 - "$candidate" <<'PY'
import copy
import sys
from zipfile import ZipFile

candidate = sys.argv[1]
with ZipFile(candidate + ".zip") as source, ZipFile("field-live-c6551c2.ipa", "w") as target:
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
PY
sha256sum "$baseline.zip" "$candidate.zip" server.tar.gz fixture.tar > inputs.sha256
python3 -m zipfile --create field-ios-honua-live.zip live.sh fixture.tar "$baseline.zip" "$candidate.zip" server.tar.gz inputs.sha256 tests wheelhouse requirements.txt
