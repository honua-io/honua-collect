#!/usr/bin/env bash
set -euo pipefail
umask 077
inputs=$PWD
work=$(mktemp -d "${TMPDIR:-/tmp}/honua-ios-live.XXXXXX")
work=$(cd "$work" && pwd -P)
mount="$work/postgres-mount"
postgres_mounted=false
mkdir -p "$mount"
cleanup() {
    if [[ $postgres_mounted = true ]] && ! hdiutil detach "$mount" >/dev/null; then
        echo "Could not detach the temporary Postgres disk image" >&2
        return
    fi
    rm -r -- "$work"
}
trap cleanup EXIT
export HONUA_E2E_REPORT_DIR="$DEVICEFARM_LOG_DIR/published-ios"
mkdir -p "$HONUA_E2E_REPORT_DIR"
exec > >(tee "$HONUA_E2E_REPORT_DIR/preflight.log") 2>&1
(
    cd "$inputs"
    shasum -a 256 --check inputs.sha256
    cp inputs.sha256 "$HONUA_E2E_REPORT_DIR/input-digests.txt"
    cp provenance.txt "$HONUA_E2E_REPORT_DIR/provenance.txt"
)
export RUNNER_TEMP="$work"
export GITHUB_WORKSPACE="$work/field"
{
    read -r HONUA_E2E_IOS_PREVIOUS_REVISION
    read -r GITHUB_SHA
    read -r HONUA_E2E_SERVER_REVISION
    read -r HONUA_E2E_UPGRADE_STATE
} < "$inputs/provenance.txt"
export HONUA_E2E_IOS_PREVIOUS_REVISION GITHUB_SHA HONUA_E2E_SERVER_REVISION HONUA_E2E_UPGRADE_STATE
download() {
    local url=$1 file=$2 expected=$3
    curl --fail --retry 3 --location --silent --show-error --output "$file" "$url"
    printf '%s  %s\n' "$expected" "$file" | shasum -a 256 --check
}
download https://github.com/PostgresApp/PostgresApp/releases/download/v2.9.7/Postgres-2.9.7-18.dmg "$work/postgres.dmg" \
    e00a4a45e322bb966253bab691c83fac06ec8eacf6833da27c98d4179f5052f6
hdiutil attach -readonly -nobrowse -mountpoint "$mount" "$work/postgres.dmg"
postgres_mounted=true
pg_path="$mount/Postgres.app/Contents/Versions/18/bin"
test -x "$pg_path/initdb"
"$pg_path/postgres" --version
download https://github.com/mobile-dev-inc/Maestro/releases/download/cli-2.12.1/maestro.zip "$work/maestro.zip" \
    54dbb0716425626604973cbb374c585ca30272375d9ce15177a1152765ddd9d7
ditto -x -k "$work/maestro.zip" "$work"
if ! JAVA_HOME=$(/usr/libexec/java_home -v 21 2>/dev/null); then
    download "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.13.1%2B2/OpenJDK21U-jre_aarch64_mac_hotspot_21.0.13.1_2.tar.gz" "$work/java.tar.gz" \
        848feb1906c7de79de4f8391636042193fbd46eff86e26299506c4656f62c260
    mkdir "$work/java"
    tar -xzf "$work/java.tar.gz" -C "$work/java"
    JAVA_HOME=$(find "$work/java" -type d -path '*/Contents/Home')
fi
export JAVA_HOME
if ! command -v jq >/dev/null; then
    download https://github.com/jqlang/jq/releases/download/jq-1.8.1/jq-macos-arm64 "$work/jq" \
        a9fe3ea2f86dfc72f6728417521ec9067b343277152b114f4e98d8cb0e263603
    chmod +x "$work/jq"
fi
export PATH="$work:$JAVA_HOME/bin:$pg_path:$work/maestro/bin:$PATH"
java -version
export HONUA_E2E_HTTP_PORT=5189
export HONUA_E2E_PUBLISH_NEXT_VERSION=1 HONUA_E2E_MIGRATION_PREFLIGHT=1
export MAESTRO_CLI_NO_ANALYTICS=1 MAESTRO_CLI_ANALYSIS_NOTIFICATION_DISABLED=true
export HONUA_E2E_IOS_DEVICE=${DEVICEFARM_DEVICE_UDID:?}
mkdir -p "$GITHUB_WORKSPACE" "$RUNNER_TEMP/published-server"
tar -xf "$inputs/fixture.tar" -C "$GITHUB_WORKSPACE"
tar -xzf "$inputs/server.tar.gz" -C "$RUNNER_TEMP"
server_apphost="$RUNNER_TEMP/published-server/Honua.Server"
test -x "$server_apphost"
file "$server_apphost"
if ! codesign --verify "$server_apphost"; then
    codesign --force --sign - "$server_apphost"
    codesign --verify "$server_apphost"
fi
build_path=src/Honua.Field.App/bin/Debug/net11.0-ios/iossimulator-arm64
mkdir -p "$GITHUB_WORKSPACE/$build_path" "$GITHUB_WORKSPACE/field-previous/$build_path"
ditto -x -k "$inputs/candidate.zip" "$GITHUB_WORKSPACE/$build_path"
ditto -x -k "$inputs/previous.zip" "$GITHUB_WORKSPACE/field-previous/$build_path"
xcrun simctl list devices booted
# Device Farm pre-installs the declared app. Remove it from this ephemeral simulator so the journey starts clean.
xcrun simctl uninstall "$HONUA_E2E_IOS_DEVICE" io.honua.collect
cd "$GITHUB_WORKSPACE"
bash .github/workflows/run-published-ios-gate.sh
