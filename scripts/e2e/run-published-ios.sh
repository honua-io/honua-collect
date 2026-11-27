#!/usr/bin/env bash
# Run on a dedicated booted Apple simulator with Field Debug already installed.
set -euo pipefail

if [[ $# -ne 3 ]]; then
    echo "Usage: $0 SIMULATOR_UDID WORKER_KEY_FILE ADMIN_KEY_FILE" >&2
    exit 2
fi

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
device=$1
worker_key=$2
admin_key=$3
port=${HONUA_E2E_HTTP_PORT:-5189}
endpoint="http://127.0.0.1:$port"
report=${HONUA_E2E_REPORT_DIR:-}
app=io.honua.collect
form=field_runtime_inspection
name="Offline Apple inspection $(date +%s)-${RANDOM}"
simctl=(xcrun simctl)
maestro=("${MAESTRO:-maestro}" --device "$device")
umask 077
working_dir=$(mktemp -d)
fault_proxy_pid=
settings_written=false
stage=0
process_watch_pid=

cleanup() {
    local status=$?
    stop_process_watch || true
    if [[ $status -ne 0 && -n $report ]]; then
        report_crash || true
        "${simctl[@]}" io "$device" screenshot "$report/failure.png" || true
        if [[ -n ${data:-} ]]; then
            report_storage failure || true
        fi
    fi
    "${simctl[@]}" terminate "$device" "$app" >/dev/null 2>&1 || true
    if $settings_written; then
        if [[ -f "$working_dir/original-settings" ]]; then
            cp -p "$working_dir/original-settings" "$settings"
        else
            rm -f -- "$settings"
        fi
    fi
    if [[ -n $fault_proxy_pid ]]; then
        kill "$fault_proxy_pid" 2>/dev/null || true
        wait "$fault_proxy_pid" 2>/dev/null || true
    fi
    rm -r -- "$working_dir"
}
trap cleanup EXIT

run_flow() {
    local flow=$1
    local options=()
    stage=$((stage + 1))
    echo "Running iOS flow $stage ($flow)"
    if [[ -n $report ]]; then
        local output="$report/$stage-$flow"
        mkdir -p -- "$output"
        options=(--format junit --output "$output/junit.xml"
                 --test-output-dir "$output" --debug-output "$output")
    fi
    "${maestro[@]}" test ${options[@]+"${options[@]}"} -e "INSPECTION_NAME=$name" \
        -e "FORM_VERSION=$version" "$script_dir/maestro/$flow.yaml"
}

launch_and_run_flow() {
    # simctl owns the cold launch; Maestro waits for a rendered Field control.
    # Without this boundary a successful UIKit launch can race Maestro's attach.
    "${simctl[@]}" launch "$device" "$app"
    run_flow "$1"
}

list_media() {
    if [[ -d "$data/Library/media" ]]; then
        find "$data/Library/media" -type f | LC_ALL=C sort
    fi
}

report_storage() {
    [[ -n $report ]] || return 0
    local container
    container=$("${simctl[@]}" get_app_container "$device" "$app" data)
    python3 - "$1" "$data" "$container" "$report/$1-storage.json" <<'PY_STORAGE'
import json
from pathlib import Path
import sys

phase, original, current, report = sys.argv[1:]
files = {}
for suffix in ("", "-wal", "-shm"):
    name = "collect-records.db" + suffix
    path = Path(current) / "Library" / name
    files[name] = path.stat().st_size if path.is_file() else None
Path(report).write_text(json.dumps({
    "phase": phase, "sameAppDataContainer": current == original, "fileSizes": files,
}) + "\n")
PY_STORAGE
}

watch_process() {
    local executable=$1
    while :; do
        ps -axww -o pid=,comm= | awk -v executable="$executable" '
            { pid = $1; sub(/^[[:space:]]*[0-9]+[[:space:]]+/, "") }
            $0 == executable { print pid; found = 1 }
            END { if (!found) print "absent" }
        '
        sleep 1
    done
}

stop_process_watch() {
    [[ -n $process_watch_pid ]] || return 0
    kill "$process_watch_pid" 2>/dev/null || true
    wait "$process_watch_pid" 2>/dev/null || true
    process_watch_pid=
    python3 - "$working_dir/process-samples" "$report/capture-process.json" <<'PY_PROCESS'
import json
from pathlib import Path
import sys

samples = Path(sys.argv[1]).read_text().splitlines()
seen = [sample for sample in samples if sample.isdecimal()]
Path(sys.argv[2]).write_text(json.dumps({
    "samples": len(samples),
    "observedAppProcess": bool(seen),
    "distinctProcessCount": len(set(seen)),
    "processPresentAtLastSample": bool(samples and samples[-1].isdecimal()),
}) + "\n")
PY_PROCESS
}

report_crash() {
    [[ -f "$working_dir/capture-start" ]] || return 0
    python3 - "$working_dir/capture-start" "$report/app-crash.json" "$app" <<'PY_CRASH'
import json
from pathlib import Path
import re
import sys

start, report, bundle_id = sys.argv[1:]
threshold = Path(start).stat().st_mtime
codes = []
for folder in (Path.home() / "Library/Logs/DiagnosticReports",
               Path("/Library/Logs/DiagnosticReports")):
    for path in folder.glob("*.ips"):
        try:
            if path.stat().st_mtime < threshold:
                continue
            metadata, body = path.read_text().split("\n", 1)
            if json.loads(metadata).get("bundleID") != bundle_id:
                continue
            crash_type = json.loads(body).get("exception", {}).get("type", "")
            if re.fullmatch(r"EXC_[A-Z_]+", crash_type):
                codes.append(crash_type)
        except (OSError, ValueError, AttributeError):
            continue
Path(report).write_text(json.dumps({"exceptionTypes": sorted(set(codes))}) + "\n")
PY_CRASH
}

if [[ -n $report ]]; then
    mkdir -p -- "$report"
    report=$(cd -- "$report" && pwd)
fi
echo "Preparing the installed Field app and local test proxy."
data=$("${simctl[@]}" get_app_container "$device" "$app" data)
settings="$data/Library/appsettings.local.json"
"${simctl[@]}" terminate "$device" "$app" >/dev/null 2>&1 || true
if [[ -f $settings ]]; then
    cp -p "$settings" "$working_dir/original-settings"
fi

python3 "$script_dir/lose-submission-receipt.py" "$port" "$form" "$working_dir" --controlled &
fault_proxy_pid=$!
for ((attempt = 0; attempt < 100; ++attempt)); do
    [[ -s "$working_dir/port" ]] && break
    kill -0 "$fault_proxy_pid"
    sleep 0.1
done
proxy_endpoint="http://localhost:$(cat "$working_dir/port")"

settings_written=true
python3 - "$proxy_endpoint" "$form" "$worker_key" "$settings" <<'PY'
import json
from pathlib import Path
import sys

endpoint, form, key_file, settings = sys.argv[1:]
key = Path(key_file).read_text().strip()
if not key:
    sys.exit("The worker API key file is empty.")
Path(settings).write_text(json.dumps({
    "server": {"baseUrl": endpoint, "formId": form}, "demo": {"apiKey": key}
}))
PY

echo "Setting simulator location and seeding the photo library."
"${simctl[@]}" privacy "$device" grant location "$app"
"${simctl[@]}" privacy "$device" grant photos "$app"
"${simctl[@]}" location "$device" set 21.31,-157.81
"${simctl[@]}" addmedia "$device" "$script_dir/maestro/inspection.png"

echo "Checking the published fixture through the proxy."
printf 'X-API-Key: %s\n' "$(cat "$worker_key")" > "$working_dir/worker-header"
version=$(curl --fail --silent --show-error --connect-timeout 5 --max-time 30 -H @"$working_dir/worker-header" \
    "$proxy_endpoint/api/v1/forms/$form" | jq -er '
    if .package.offlinePolicy.enabled != true then error("The published fixture must allow offline capture.")
    else .version | select(type == "number" and . >= 1) end')
list_media > "$working_dir/before"
launch_and_run_flow download-ios
"${simctl[@]}" terminate "$device" "$app"

# Simulate server unreachability without affecting the macOS host or other devices.
touch "$working_dir/offline"
if curl --silent --fail --max-time 3 --output /dev/null "$proxy_endpoint/api/v1/forms/$form"; then
    echo "The simulator endpoint still responds in offline mode." >&2
    exit 1
fi
test -f "$working_dir/blocked"
if [[ -n $report ]]; then
    touch "$working_dir/capture-start"
    bundle=$("${simctl[@]}" get_app_container "$device" "$app" app)
    executable=$(plutil -extract CFBundleExecutable raw -o - "$bundle/Info.plist")
    watch_process "$bundle/$executable" > "$working_dir/process-samples" &
    process_watch_pid=$!
fi
launch_and_run_flow capture-ios
stop_process_watch
report_storage after-draft-save
"${simctl[@]}" terminate "$device" "$app"
report_storage after-draft-stop
latest_version=$("$script_dir/publish-next-inspection-version.sh" "$port" "$version" "$admin_key")
launch_and_run_flow resume-draft-ios
"${simctl[@]}" terminate "$device" "$app"
launch_and_run_flow verify-outbox-ios

list_media > "$working_dir/after"
LC_ALL=C comm -13 "$working_dir/before" "$working_dir/after" > "$working_dir/new-media"
if [[ $(wc -l < "$working_dir/new-media") -ne 1 ]]; then
    echo "Expected one stored Apple attachment to survive process restart." >&2
    exit 1
fi
attachment=$(cat "$working_dir/new-media")
test -s "$attachment"
cp -- "$attachment" "$working_dir/attachment"

touch "$working_dir/lose-receipt"
rm -- "$working_dir/offline"
run_flow retry-outbox
test -f "$working_dir/withheld"
"${simctl[@]}" terminate "$device" "$app"
launch_and_run_flow verify-outbox-ios
rm -- "$working_dir/lose-receipt"
run_flow sync

readback_report=()
if [[ -n $report ]]; then
    readback_report=(--report "$report/server-readback.json")
fi
python3 "$script_dir/assert-field-record.py" --server "$endpoint" --api-key-file "$admin_key" \
    --form "$form" --version "$version" --latest-version "$latest_version" \
    --name "$name" --status open --status closed --total 2 \
    --repeat-note 'Offline visit one' --repeat-note 'Offline visit two' \
    --longitude=-157.81 --latitude=21.31 --attachment "$working_dir/attachment" ${readback_report[@]+"${readback_report[@]}"}
