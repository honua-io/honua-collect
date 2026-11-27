#!/usr/bin/env bash
set -euo pipefail
umask 077
setup=$(cd "$(dirname -- "$0")" && pwd)
field=$(git -C "$setup" rev-parse --show-toplevel)
server_revision=${HONUA_E2E_SERVER_REVISION:-ea127fc0916f2bce70596deb7e4f11784ce9f7d1}
field_revision=${HONUA_E2E_FIELD_REVISION:-c6551c28995ed4f15cd9db92ec5df95938ae0da4}
previous_revision=${HONUA_E2E_IOS_PREVIOUS_REVISION:-6e3db0b0c48c63d1bdb6fd1d7f00c2030dc33c46}
upgrade_state=${HONUA_E2E_UPGRADE_STATE:-outbox}
picker_revision=${HONUA_E2E_IOS_PICKER_REVISION:-394051063939253a18e67d45fa76ef4ed509b364}
harness_revision=$(git -C "$field" rev-parse HEAD)
if ! git -C "$field" diff --quiet "$harness_revision" -- scripts/e2e/aws-ios-live/stage.sh; then
    echo "Commit stage.sh before staging a pinned run" >&2
    exit 2
fi
for revision in "$server_revision" "$field_revision" "$previous_revision" "$picker_revision"; do
    [[ "$revision" =~ ^[0-9a-f]{40}$ ]] || { echo "Expected a full Git revision" >&2; exit 2; }
done
case "$upgrade_state" in
    draft|outbox) ;;
    *) echo "Upgrade state must be draft or outbox" >&2; exit 2 ;;
esac
if [ "$#" -ne 5 ]; then
    echo "Usage: $0 <new-output-directory> <app-artifact-directory> <server-git-checkout> <server-tar.gz> <server-sha256-file>" >&2
    exit 2
fi
runner=$(git -C "$field" show "$field_revision:scripts/e2e/run-published-ios.sh")
if [[ "$runner" != *HONUA_E2E_IOS_UPGRADE_APP* || "$runner" != *installed-upgrade.json* ]]; then
    echo "The selected Field revision must support upgrade installation and record its evidence." >&2
    exit 2
fi
destination=$1
mkdir -- "$destination"
destination=$(cd "$destination" && pwd)
harness_path=scripts/e2e/aws-ios-live
git -C "$field" archive "$harness_revision" "$harness_path" | tar -x --strip-components=3 -C "$destination"
(
    cd "$2"
    for revision in "$previous_revision" "$field_revision"; do
        digest=$(awk 'NR == 1 {print $1}' "$revision.zip.sha256")
        printf '%s  %s\n' "$digest" "$revision.zip" | sha256sum --check -
    done
    cp "$previous_revision.zip" "$destination/previous.zip"
    cp "$field_revision.zip" "$destination/candidate.zip"
)
printf '%s\n' "$previous_revision" "$field_revision" "$server_revision" "$upgrade_state" \
    "$harness_revision" "$picker_revision" > "$destination/provenance.txt"

digest=$(awk 'NR == 1 {print $1}' "$5")
printf '%s  %s\n' "$digest" "$4" | sha256sum --check -
cp "$4" "$destination/server.tar.gz"
fixture="$destination/fixture"
mkdir -p "$fixture"/{server-candidate/tests/seed,.github/workflows}
git -C "$field" archive "$field_revision" scripts/e2e | tar -x -C "$fixture"
git -C "$field" show "$picker_revision:scripts/e2e/maestro/capture-ios.yaml" \
    > "$fixture/scripts/e2e/maestro/capture-ios.yaml"
git -C "$field" archive "$harness_revision" .github/workflows/run-published-ios-gate.sh | tar -x -C "$fixture"
git -C "$3" show "$server_revision:tests/seed/base-schema.sql" > "$fixture/server-candidate/tests/seed/base-schema.sql"
tar -cf "$destination/fixture.tar" -C "$fixture" .
# The upload validator expects a pytest package. The custom test spec
# starts the shell gate, which drives the same simulator with Maestro.
python3 -m pip download --only-binary=:all: --dest "$destination/wheelhouse" -r "$destination/requirements.txt"
bash "$destination/pack.sh"
