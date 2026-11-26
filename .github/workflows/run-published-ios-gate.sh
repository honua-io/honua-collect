#!/usr/bin/env bash
set -euo pipefail
umask 077

workspace=${GITHUB_WORKSPACE:?}
candidate="$workspace/server-candidate"
working_dir=$(mktemp -d "${RUNNER_TEMP:?}/published-ios.XXXXXX")
report_dir=${HONUA_E2E_REPORT_DIR:?}
mkdir -p "$report_dir"
device_created=''

run_bounded() {
    python3 -c 'import subprocess, sys; sys.exit(subprocess.run(sys.argv[2:], timeout=int(sys.argv[1])).returncode)' "$@"
}

cleanup() {
    if [[ -n $device_created ]]; then
        run_bounded 90 xcrun simctl shutdown "$device_created" || true
        run_bounded 90 xcrun simctl delete "$device_created" || true
    fi
    if [[ -s "$working_dir/server.pid" ]]; then
        kill "$(cat "$working_dir/server.pid")" 2>/dev/null || true
    fi
    if [[ -s "$working_dir/pg/postmaster.pid" ]]; then
        pg_ctl -D "$working_dir/pg" -m fast -w -t 30 stop || true
    fi
    rm -r -- "$working_dir"
}
trap cleanup EXIT

export PGPORT=55488
export PGPASSFILE="$working_dir/pgpass"
db_password=$(openssl rand -hex 24)
printf '127.0.0.1:%s:*:postgres:%s\n' "$PGPORT" "$db_password" > "$PGPASSFILE"
printf '%s\n' "$db_password" > "$working_dir/db-password"
initdb -D "$working_dir/pg" --username=postgres --pwfile="$working_dir/db-password" \
    --auth-local=scram-sha-256 --auth-host=scram-sha-256 > "$working_dir/initdb.log"
# Suppress Unix socket creation. The cluster uses a private data directory and listens on loopback only.
pg_ctl -D "$working_dir/pg" -l "$working_dir/postgres.log" -o "-h 127.0.0.1 -p $PGPORT -c unix_socket_directories=''" \
    -w -t 60 start
createdb -w -h 127.0.0.1 -p "$PGPORT" -U postgres honua
psql -X -w -h 127.0.0.1 -p "$PGPORT" -U postgres -d honua -v ON_ERROR_STOP=1 \
    -c 'CREATE EXTENSION postgis' -c 'SELECT postgis_full_version()'

openssl rand -hex 24 > "$working_dir/admin-key"
openssl rand -base64 32 > "$working_dir/master-key"
openssl rand -base64 16 > "$working_dir/encryption-salt"

export HONUA_E2E_SERVER_STATE="$working_dir"
export HONUA_E2E_WORKER_KEY_FILE="$working_dir/worker-key"
export HONUA_E2E_PG_NATIVE=1 HONUA_E2E_PG_DATABASE=honua HONUA_E2E_PG_USER=postgres
export HONUA_E2E_RESTART_SERVER_COMMAND="$working_dir/restart-server"
cat > "$HONUA_E2E_RESTART_SERVER_COMMAND" <<'RESTART'
#!/usr/bin/env bash
set -euo pipefail
state=$HONUA_E2E_SERVER_STATE
pid_file="$state/server.pid"
if [[ -s $pid_file ]]; then
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
base_url="http://127.0.0.1:${HONUA_E2E_HTTP_PORT:?}"
export ASPNETCORE_ENVIRONMENT=Development
export Kestrel__Endpoints__Http__Url="$base_url" Kestrel__Endpoints__Http__Protocols=Http1
export ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=$PGPORT;Database=honua;Username=postgres;Password=$(cat "$state/db-password")"
export HONUA_ADMIN_PASSWORD="$(cat "$state/admin-key")"
export Security__ConnectionEncryption__MasterKey="$(cat "$state/master-key")"
export Security__ConnectionEncryption__Salt="$(cat "$state/encryption-salt")"
export Database__MigrationSafety__ContractApplyPolicy=Auto
export Authentication__PortalToken__Enabled=true Authentication__PortalToken__RequireHttps=false
export Authentication__PortalCredentialVerifier__FallBackToAdminVerifier=true
export FileStorage__Provider=Local FileStorage__LocalStorage__BasePath="$state/attachments"
export Geocoding__Enabled=false
cd "$RUNNER_TEMP/published-server"
nohup ./Honua.Server </dev/null >> "$state/server.log" 2>&1 &
echo "$!" > "$pid_file"
for ((attempt = 0; attempt < 90; ++attempt)); do
    if curl --fail --silent --max-time 2 "$base_url/healthz/ready" >/dev/null; then
        exit 0
    fi
    if ! kill -0 "$(cat "$pid_file")" 2>/dev/null; then
        echo "::error::The fixture server exited before becoming ready" >&2
        exit 1
    fi
    sleep 2
done
echo "::error::Timed out waiting for the fixture server" >&2
exit 1
RESTART
chmod 700 "$HONUA_E2E_RESTART_SERVER_COMMAND"
"$HONUA_E2E_RESTART_SERVER_COMMAND"
"$workspace/scripts/e2e/publish-field-runtime-fixture.sh" "$candidate" "$working_dir/admin-key"

runtime=com.apple.CoreSimulator.SimRuntime.iOS-27-2
xcrun simctl list devices available --json > "$report_dir/simulators.json"
device_type=$(jq -er --arg runtime "$runtime" '
    .devices[$runtime] | map(select(.name | startswith("iPhone"))) | first | .deviceTypeIdentifier
' "$report_dir/simulators.json")
device_created=$(run_bounded 60 xcrun simctl create 'Field Published Forms iPhone' "$device_type" "$runtime")
run_bounded 540 xcrun simctl bootstatus "$device_created" -b
app=$(find "$workspace/src/Honua.Field.App/bin/Debug/net11.0-ios/iossimulator-arm64" -maxdepth 1 -name '*.app' -type d)
test -d "$app"
run_bounded 480 xcrun simctl install "$device_created" "$app"

if ! run_bounded 1500 bash "$workspace/scripts/e2e/run-published-ios.sh" \
    "$device_created" "$HONUA_E2E_WORKER_KEY_FILE" "$working_dir/admin-key"; then
    if [[ ! -s "$report_dir/failure.png" ]]; then
        run_bounded 60 xcrun simctl io "$device_created" screenshot "$report_dir/failure.png" || true
    fi
    exit 1
fi
