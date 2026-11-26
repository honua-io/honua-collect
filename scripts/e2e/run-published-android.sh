#!/usr/bin/env bash
# Run with a dedicated Android emulator, an installed Debug APK, and the
# field_runtime_inspection fixture published on the local E2E server.
set -euo pipefail

if [[ $# -lt 2 || $# -gt 3 ]]; then
    echo "Usage: $0 ANDROID_SERIAL WORKER_KEY_FILE [READBACK_KEY_FILE]" >&2
    exit 2
fi

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
serial=$1
key_file=$2
readback_key_file=${3:-$key_file}
port=${HONUA_E2E_HTTP_PORT:-5189}
endpoint="http://127.0.0.1:${port}"
app=io.honua.collect
form=field_runtime_inspection
name="Offline inspection $(date +%s)-${RANDOM}"
adb=("${ADB:-adb}" -s "$serial")
maestro=("${MAESTRO:-maestro}" --device "$serial")
working_dir=$(mktemp -d)
fault_proxy_pid=
reverse_started=false
settings_written=false
latest_version=
flow_number=0
umask 077

cleanup() {
    local result=$?
    if [[ $result -ne 0 && -n ${HONUA_E2E_REPORT_DIR:-} ]]; then
        local screenshot="$HONUA_E2E_REPORT_DIR/failure.png"
        mkdir -p -- "$HONUA_E2E_REPORT_DIR"
        if ! "${adb[@]}" exec-out screencap -p > "$screenshot" || [[ ! -s $screenshot ]]; then
            rm -f -- "$screenshot"
        fi
    fi
    if [[ -n $fault_proxy_pid ]]; then
        kill "$fault_proxy_pid" 2>/dev/null || true
        wait "$fault_proxy_pid" 2>/dev/null || true
    fi
    if $reverse_started; then
        "${adb[@]}" reverse --remove "tcp:${port}" >/dev/null 2>&1 || true
    fi
    if $settings_written; then
        "${adb[@]}" shell am force-stop "$app" >/dev/null 2>&1 || true
        if [[ -f "$working_dir/original-settings" ]]; then
            "${adb[@]}" shell "run-as $app sh -c 'cat > files/appsettings.local.json'" \
                < "$working_dir/original-settings" || true
        else
            "${adb[@]}" shell run-as "$app" rm -f files/appsettings.local.json || true
        fi
    fi
    rm -r -- "$working_dir"
}

trap cleanup EXIT

list_media() {
    "${adb[@]}" shell "run-as $app sh -c 'if [ -d files/media ]; then find files/media -type f; fi'"         | tr -d '\r' | LC_ALL=C sort
}

run_flow() {
    local flow=$1
    local report=()
    if [[ -n ${HONUA_E2E_REPORT_DIR:-} ]]; then
        mkdir -p -- "$HONUA_E2E_REPORT_DIR"
        flow_number=$((flow_number + 1))
        report=(--format junit --output "$HONUA_E2E_REPORT_DIR/$flow_number-$flow.xml")
    fi
    "${maestro[@]}" test "${report[@]}" -e "INSPECTION_NAME=$name" \
        -e "CORRECTED_NAME=${HONUA_E2E_CORRECTED_NAME:-}" -e "FORM_VERSION=$version" "$script_dir/maestro/$flow.yaml"
}

"${adb[@]}" shell run-as "$app" id >/dev/null
"${adb[@]}" shell am force-stop "$app"
if "${adb[@]}" shell run-as "$app" test -f files/appsettings.local.json; then
    "${adb[@]}" exec-out run-as "$app" cat files/appsettings.local.json > "$working_dir/original-settings"
fi
settings_written=true
python3 - "$endpoint" "$form" "$key_file" <<'PY' | "${adb[@]}" shell "run-as $app sh -c 'mkdir -p files && cat > files/appsettings.local.json'"
import json
import pathlib
import sys

endpoint, form, key_file = sys.argv[1:]
key = pathlib.Path(key_file).read_text().strip()
if not key:
    sys.exit("The API key file is empty.")
json.dump({"server": {"baseUrl": endpoint, "formId": form}, "demo": {"apiKey": key}}, sys.stdout)
PY

"${adb[@]}" reverse "tcp:${port}" "tcp:${port}" >/dev/null
reverse_started=true
for permission in ACCESS_COARSE_LOCATION ACCESS_FINE_LOCATION; do
    "${adb[@]}" shell pm grant "$app" "android.permission.$permission"
done
if [[ -n ${HONUA_E2E_EMULATOR_CONTAINER:-} ]]; then
    docker exec "$HONUA_E2E_EMULATOR_CONTAINER" adb -s emulator-5554 emu geo fix -157.81 21.31
else
    "${adb[@]}" emu geo fix -157.81 21.31
fi
printf 'X-API-Key: %s\n' "$(cat "$key_file")" > "$working_dir/worker-header"
version=$(curl --fail --silent --show-error -H @"$working_dir/worker-header" "$endpoint/api/v1/forms/$form" \
    | jq -er 'if .package.offlinePolicy.enabled != true then
                  error("The published fixture must allow offline capture.")
              else
                  .version | select(type == "number" and . >= 1)
              end')
list_media > "$working_dir/before"
run_flow download
"${adb[@]}" reverse --remove "tcp:${port}"
"${adb[@]}" shell am force-stop "$app"

run_flow capture
"${adb[@]}" shell am force-stop "$app"
if [[ ${HONUA_E2E_PUBLISH_NEXT_VERSION:-0} == 1 ]]; then
    # The device is still offline. Only the host has the fixture admin key.
    latest_version=$("$script_dir/publish-next-inspection-version.sh" "$port" "$version" "${3:?Pass the fixture admin key file to publish another version}")
fi
run_flow resume-draft
"${adb[@]}" shell am force-stop "$app"
run_flow verify-outbox

list_media > "$working_dir/after"
mapfile -t new_media < <(LC_ALL=C comm -13 "$working_dir/before" "$working_dir/after")
if [[ ${#new_media[@]} -ne 1 ]]; then
    echo "Expected one new stored attachment after restart, found ${#new_media[@]}." >&2
    exit 1
fi
"${adb[@]}" exec-out run-as "$app" cat "${new_media[0]}" > "$working_dir/attachment"
test -s "$working_dir/attachment"

if [[ -n ${HONUA_E2E_CORRECTED_NAME:-} ]]; then
    touch "${HONUA_E2E_REJECTION_LIMIT_MARKER:?Use run-rejected-android.sh}"
    "$script_dir/set-inspection-name-limit.sh" 16 "$key_file"
    "${adb[@]}" reverse "tcp:${port}" "tcp:${port}" >/dev/null
    run_flow reject-outbox
    "${adb[@]}" shell am force-stop "$app"
    "${adb[@]}" reverse --remove "tcp:${port}"
    run_flow correct-rejection
    "${adb[@]}" shell am force-stop "$app"
    run_flow verify-outbox
    "${adb[@]}" reverse "tcp:${port}" "tcp:${port}" >/dev/null
    run_flow sync
    name=$HONUA_E2E_CORRECTED_NAME
else
    # The server commits the first upload, but Field sees a transient network error.
    # Restart with its failed outbox on disk and deliver the real receipt on retry.
    python3 "$script_dir/lose-submission-receipt.py" "$port" "$form" "$working_dir" &
    fault_proxy_pid=$!
    for ((attempt = 0; attempt < 100; ++attempt)); do
        [[ -s "$working_dir/port" ]] && break
        kill -0 "$fault_proxy_pid"
        sleep 0.1
    done
    proxy_port=$(cat "$working_dir/port")
    "${adb[@]}" reverse "tcp:${port}" "tcp:${proxy_port}" >/dev/null
    run_flow retry-outbox
    test -f "$working_dir/withheld"
    "${adb[@]}" shell am force-stop "$app"
    run_flow verify-outbox
    "${adb[@]}" reverse "tcp:${port}" "tcp:${port}" >/dev/null
    run_flow sync
fi
readback_report=()
if [[ -n ${HONUA_E2E_REPORT_DIR:-} ]]; then
    readback_report=(--report "$HONUA_E2E_REPORT_DIR/server-readback.json")
fi
if [[ -n $latest_version ]]; then
    readback_report+=(--latest-version "$latest_version")
fi
python3 "$script_dir/assert-field-record.py" --server "$endpoint" --api-key-file "$readback_key_file" \
    --form "$form" --version "$version" --name "$name" --status open --status closed --total 2 \
    --repeat-note 'Offline visit one' --repeat-note 'Offline visit two' \
    --longitude=-157.81 --latitude=21.31 --attachment "$working_dir/attachment" "${readback_report[@]}"
