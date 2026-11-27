#!/usr/bin/env bash
set -euo pipefail

workspace="$GITHUB_WORKSPACE"
container=${HONUA_E2E_PG_CONTAINER:?}
candidate="$workspace/server-candidate"
working_dir=$(mktemp -d "$RUNNER_TEMP/published-android.XXXXXX")
umask 077

cleanup() {
    if [[ -f "$working_dir/server.pid" ]]; then
        kill "$(cat "$working_dir/server.pid")" 2>/dev/null || true
    fi
    docker rm -f "$container" >/dev/null 2>&1 || true
    rm -r -- "$working_dir"
}
trap cleanup EXIT

openssl rand -hex 24 > "$working_dir/admin-key"
db_password=$(openssl rand -hex 24)
printf 'POSTGRES_DB=honua\nPOSTGRES_PASSWORD=%s\n' "$db_password" > "$working_dir/postgres.env"
docker run --detach --name "$container" --publish 127.0.0.1:55488:5432 \
    --env-file "$working_dir/postgres.env" "${POSTGIS_IMAGE:?}" >/dev/null
database_ready() {
    docker exec "$container" pg_isready -h 127.0.0.1 -U postgres -d honua >/dev/null 2>&1
}
wait_for() {
    local check=$1
    local attempt
    for ((attempt = 0; attempt < 90; ++attempt)); do
        if "$check"; then
            return
        fi
        sleep 2
    done
    echo "::error::Timed out waiting for $check" >&2
    return 1
}
wait_for database_ready
openssl rand -base64 32 > "$working_dir/master-key"
openssl rand -base64 16 > "$working_dir/encryption-salt"

export HONUA_E2E_SERVER_STATE="$working_dir"
export HONUA_E2E_WORKER_KEY_FILE="$working_dir/worker-key"
export HONUA_E2E_RESTART_SERVER_COMMAND="$working_dir/restart-server"
cat > "$HONUA_E2E_RESTART_SERVER_COMMAND" <<'RESTART'
#!/usr/bin/env bash
set -euo pipefail
pid_file="$HONUA_E2E_SERVER_STATE/server.pid"
if [[ -f "$pid_file" ]]; then
    old_pid=$(cat "$pid_file")
    kill "$old_pid"
    for ((attempt = 0; attempt < 50; ++attempt)); do
        if ! kill -0 "$old_pid" 2>/dev/null; then
            break
        fi
        sleep 0.2
    done
    if kill -0 "$old_pid" 2>/dev/null; then
        echo "::error::The server did not stop for fixture activation" >&2
        exit 1
    fi
fi
source "$HONUA_E2E_SERVER_STATE/postgres.env"
base_url="http://127.0.0.1:${HONUA_E2E_HTTP_PORT:?}"
export ASPNETCORE_ENVIRONMENT=Development
export Kestrel__Endpoints__Http__Url="$base_url" Kestrel__Endpoints__Http__Protocols=Http1
export ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=55488;Database=honua;Username=postgres;Password=${POSTGRES_PASSWORD:?}"
export HONUA_ADMIN_PASSWORD="$(cat "$HONUA_E2E_SERVER_STATE/admin-key")"
export Security__ConnectionEncryption__MasterKey="$(cat "$HONUA_E2E_SERVER_STATE/master-key")"
export Security__ConnectionEncryption__Salt="$(cat "$HONUA_E2E_SERVER_STATE/encryption-salt")"
export Database__MigrationSafety__ContractApplyPolicy=Auto
export Authentication__PortalToken__Enabled=true Authentication__PortalToken__RequireHttps=false
export Authentication__PortalCredentialVerifier__FallBackToAdminVerifier=true
export FileStorage__Provider=Local FileStorage__LocalStorage__BasePath="$HONUA_E2E_SERVER_STATE/attachments"
export Geocoding__Enabled=false
cd "$RUNNER_TEMP/published-server"
nohup ./Honua.Server </dev/null >> "$HONUA_E2E_SERVER_STATE/server.log" 2>&1 &
echo "$!" > "$pid_file"
for ((attempt = 0; attempt < 90; ++attempt)); do
    if curl --fail --silent --max-time 2 "$base_url/healthz/ready" >/dev/null; then
        exit 0
    fi
    kill -0 "$(cat "$pid_file")" || exit 1
    sleep 2
done
echo "::error::Timed out waiting for the restarted fixture server" >&2
exit 1
RESTART
chmod 700 "$HONUA_E2E_RESTART_SERVER_COMMAND"
"$HONUA_E2E_RESTART_SERVER_COMMAND"
"$workspace/scripts/e2e/publish-field-runtime-fixture.sh" "$candidate" "$working_dir/admin-key"

serial=emulator-5554
report_root=${HONUA_E2E_REPORT_DIR:?}
APKSIGNER=$(find "${ANDROID_HOME:?}/build-tools" -name apksigner -type f | sort -V | tail -n 1)
export APKSIGNER
test -x "$APKSIGNER"
adb_device=(adb -s "$serial")
test "$("${adb_device[@]}" shell getprop ro.kernel.qemu)" = 1
package=io.honua.collect
apk="$RUNNER_TEMP/honua-field.apk"
failed=0

fail_gate() {
    echo "::error::$1" >&2
    failed=1
}

# Each candidate check starts with an empty sandbox, an explicit display size,
# and no stale reverse forwarding from a failed upgrade or prior check.
prepare_candidate_check() {
    local size=$1 density=$2 output
    output=$("${adb_device[@]}" shell pm clear "$package") &&
        [[ $output == Success ]] &&
        "${adb_device[@]}" reverse --remove-all &&
        "${adb_device[@]}" shell wm size "$size" &&
        "${adb_device[@]}" shell wm density "$density"
}

# Upgrade phases depend on the preceding phase. Leave historical data intact
# until upgrade/recovery completes or fails and captures its evidence.
"${adb_device[@]}" shell wm size 822x1782
"${adb_device[@]}" shell wm density 320
upgrade=(python3 "$workspace/scripts/e2e/run-installed-published-android-upgrade.py" "$serial" "$report_root/upgrade")
if ! "${upgrade[@]}" seed "$RUNNER_TEMP/honua-field-previous.apk" "$HONUA_E2E_WORKER_KEY_FILE"; then
    fail_gate "Historical Android seed failed"
elif ! "${upgrade[@]}" verify "$apk"; then
    fail_gate "In-place Android upgrade failed"
elif ! "${upgrade[@]}" recover-key; then
    fail_gate "Installed Android key recovery failed"
elif ! "${upgrade[@]}" recover-outbox "$working_dir/admin-key"; then
    fail_gate "Historical Android outbox recovery failed"
fi

# Use the candidate even if the upgrade failed before installing it. A failed
# candidate install stops these checks, which must never run on the historical APK.
if ! install_result=$("${adb_device[@]}" install --no-streaming -r "$apk") ||
    ! grep -Fxq Success <<<"${install_result//$'\r'/}"; then
    echo "::error::Candidate Android install failed. Independent checks cannot run." >&2
    exit 1
fi

if prepare_candidate_check 822x1782 320; then
    if ! python3 "$workspace/scripts/e2e/check-adaptive-geometry.py" "$serial" "$report_root/adaptive"; then
        fail_gate "Android adaptive geometry failed"
    fi
else
    fail_gate "Android adaptive geometry setup failed"
fi

if prepare_candidate_check 822x1782 320; then
    if ! HONUA_E2E_REPORT_DIR="$report_root/receipt-replay" \
        "$workspace/scripts/e2e/run-published-android.sh" "$serial" "$HONUA_E2E_WORKER_KEY_FILE" "$working_dir/admin-key"; then
        fail_gate "Android receipt replay failed"
    fi
else
    fail_gate "Android receipt replay setup failed"
fi

# Tablet portrait, 800 x 1280 dp. The correction check scopes its own server
# ledger assertions to this run, even if receipt replay failed.
if prepare_candidate_check 1200x1920 240; then
    if ! HONUA_E2E_REPORT_DIR="$report_root/rejection-correction" HONUA_E2E_TEST_POSTGIS_CONTAINER="$container" \
        "$workspace/scripts/e2e/run-rejected-android.sh" "$serial" "$HONUA_E2E_WORKER_KEY_FILE" "$working_dir/admin-key"; then
        fail_gate "Android rejection and correction failed"
    fi
else
    fail_gate "Android rejection and correction setup failed"
fi

# The ordinary feature layer uses its own metadata snapshot. Activate it after
# the published-workbook flows have finished exercising historical versions.
if "$workspace/scripts/e2e/seed-feature-sync.sh" "$candidate" "$container" postgres honua &&
    Licensing__DevGrantEdition=Pro "$HONUA_E2E_RESTART_SERVER_COMMAND"; then
    if ! HONUA_E2E_SERVER="http://127.0.0.1:${HONUA_E2E_HTTP_PORT:?}" HONUA_E2E_APIKEY="$(cat "$working_dir/admin-key")" \
        "$RUNNER_TEMP/field-dotnet/dotnet" test "$workspace/tests/Honua.Field.Core.Tests" --no-build --no-restore \
        --filter FullyQualifiedName~FeatureSyncE2ETests --logger 'trx;LogFileName=feature-sync.trx' \
        --results-directory "$report_root/feature-sync"; then
        fail_gate "Live feature sync integration tests failed"
    elif ! python3 - "$report_root/feature-sync/feature-sync.trx" <<'PYTEST'
import sys
import xml.etree.ElementTree as ET

results = ET.parse(sys.argv[1]).findall(".//{*}UnitTestResult")
required = {
    "Batch_add_persists_each_feature_once_and_returns_matching_object_ids",
    "Ordinary_attachment_round_trips_metadata_and_photo_bytes",
}
executed = {result.get("testName", "").rsplit(".", 1)[-1] for result in results}
if missing := required - executed:
    sys.exit("Missing live feature sync evidence: " + ", ".join(sorted(missing)))
if any(result.get("outcome") != "Passed" for result in results):
    sys.exit("The live feature sync suite must execute every selected test successfully")
PYTEST
    then
        fail_gate "Live feature sync integration evidence is incomplete"
    fi
else
    fail_gate "Live feature sync integration fixture failed"
fi

exit "$failed"
