#!/usr/bin/env bash
set -euo pipefail
umask 077
setup=$(cd "$(dirname -- "$0")" && pwd)
field=$(git -C "$setup" rev-parse --show-toplevel)
server_revision=ea127fc0916f2bce70596deb7e4f11784ce9f7d1
field_revision=c6551c28995ed4f15cd9db92ec5df95938ae0da4
if [ "$#" -ne 5 ]; then
    echo "Usage: $0 <new-output-directory> <app-artifact-directory> <server-git-checkout> <server-tar.gz> <server-sha256-file>" >&2
    exit 2
fi
destination=$1
mkdir -- "$destination"
destination=$(cd "$destination" && pwd)
cp "$setup"/{live.sh,live.yml,schedule.py,pack.sh,requirements.txt} "$destination/"
cp -R "$setup/tests" "$destination/"
cp "$2"/*.zip "$destination/"
cat "$2"/*.zip.sha256 > "$destination/app-digests.sha256"
digest=$(awk 'NR == 1 {print $1}' "$5")
printf '%s  %s\n' "$digest" "$4" | sha256sum --check -
cp "$4" "$destination/server.tar.gz"
fixture="$destination/fixture"
mkdir -p "$fixture"/{server-candidate/tests/seed,.github/workflows}
git -C "$field" archive "$field_revision" scripts/e2e | tar -x -C "$fixture"
cp "$field/.github/workflows/run-published-ios-gate.sh" "$fixture/.github/workflows/"
git -C "$3" show "$server_revision:tests/seed/base-schema.sql" > "$fixture/server-candidate/tests/seed/base-schema.sql"
tar -cf "$destination/fixture.tar" -C "$fixture" .
# The upload validator expects a pytest package. The custom test spec
# starts the shell gate, which drives the same simulator with Maestro.
python3 -m pip download --only-binary=:all: --dest "$destination/wheelhouse" -r "$destination/requirements.txt"
bash "$destination/pack.sh"
