#!/usr/bin/env bash
# Run with a dedicated Android emulator, an installed Debug APK, and the
# field_runtime_inspection fixture published on the local E2E server.
set -euo pipefail

if [[ $# -ne 2 ]]; then
    echo "Usage: $0 ANDROID_SERIAL API_KEY_FILE" >&2
    exit 2
fi

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
serial=$1
key_file=$2
port=${HONUA_E2E_HTTP_PORT:-5189}
endpoint="http://127.0.0.1:${port}"
app=io.honua.collect
form=field_runtime_inspection
name="Offline inspection $(date +%s)-${RANDOM}"
adb=("${ADB:-adb}" -s "$serial")
maestro=("${MAESTRO:-maestro}" --device "$serial")
working_dir=$(mktemp -d)
reverse_started=false
settings_written=false

cleanup() {
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
    "${maestro[@]}" test -e "INSPECTION_NAME=$name" "$script_dir/maestro/$1.yaml"
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
list_media > "$working_dir/before"
run_flow download
"${adb[@]}" reverse --remove "tcp:${port}"
"${adb[@]}" shell am force-stop "$app"

run_flow capture
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

"${adb[@]}" reverse "tcp:${port}" "tcp:${port}" >/dev/null
run_flow sync
run_flow sync
python3 "$script_dir/assert-field-record.py" --server "$endpoint" --api-key-file "$key_file" \
    --form "$form" --name "$name" --status open --status closed --total 2 \
    --repeat-note 'Offline visit one' --repeat-note 'Offline visit two' \
    --longitude=-157.81 --latitude=21.31 --attachment "$working_dir/attachment"
