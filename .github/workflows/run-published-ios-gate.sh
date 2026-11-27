#!/usr/bin/env bash
set -euo pipefail
umask 077

workspace=${GITHUB_WORKSPACE:?}
candidate="$workspace/server-candidate"
working_dir=$(mktemp -d "${RUNNER_TEMP:?}/published-ios.XXXXXX")
report_dir=${HONUA_E2E_REPORT_DIR:?}
mkdir -p "$report_dir"
device=${HONUA_E2E_IOS_DEVICE:?}

run_bounded() {
    python3 -c 'import subprocess, sys; sys.exit(subprocess.run(sys.argv[2:], timeout=int(sys.argv[1])).returncode)' "$@"
}

cleanup() {
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
    --encoding=UTF8 --locale=C --auth-local=scram-sha-256 --auth-host=scram-sha-256 > "$working_dir/initdb.log"
# Suppress Unix socket creation. The cluster uses a private data directory and listens on loopback only.
pg_ctl -D "$working_dir/pg" -l "$working_dir/postgres.log" -o "-h 127.0.0.1 -p $PGPORT -c unix_socket_directories=''" \
    -w -t 60 start
createdb -w -h 127.0.0.1 -p "$PGPORT" -U postgres honua
encoding=$(psql -X -A -t -w -h 127.0.0.1 -p "$PGPORT" -U postgres -d honua -v ON_ERROR_STOP=1 -c 'SHOW server_encoding')
if [[ $encoding != UTF8 ]]; then
    echo "Expected a UTF8 fixture database, got $encoding" >&2
    exit 1
fi
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
report_startup_diagnostics() {
    python3 - "$state" <<'PYTHON'
from collections import deque
from itertools import islice
from pathlib import Path
import re
import sys
from urllib.parse import quote, quote_plus

state = Path(sys.argv[1])
secrets = set()
for filename in ("db-password", "admin-key", "master-key", "encryption-salt", "worker-key"):
    secret_file = state / filename
    if secret_file.exists():
        value = secret_file.read_text().strip()
        if value:
            secrets.update((value, quote(value, safe=""), quote_plus(value, safe="")))
for secret in tuple(secrets):
    secrets.add(re.sub(r"%[0-9A-F]{2}", lambda match: match[0].lower(), secret))

patterns = {
    "migration.log": r"migrat|error|fail|exception|schema",
    "server.log": r"\b(?:WRN|ERR|FTL|warn|fail|critical)\b|migrat|now listening|exception",
    "server-host.trace": r"error|failed|not found|could not|permission denied",
    "postgres.log": r"\b(?:ERROR|FATAL|PANIC|DETAIL|HINT)\b",
}
redactions = sorted(secrets, key=len, reverse=True)
for filename, pattern in patterns.items():
    log = state / filename
    if not log.exists():
        continue
    with log.open(errors="replace") as entries:
        matching = (line for line in entries if re.search(pattern, line, re.IGNORECASE))
        selected = [*islice(matching, 24), *deque(matching, maxlen=12)]
    print(f"Startup diagnostics from {filename}", file=sys.stderr)
    for line in selected:
        for secret in redactions:
            line = line.replace(secret, "[REDACTED]")
        print(line.rstrip()[:1500], file=sys.stderr)
PYTHON
}
cd "$RUNNER_TEMP/published-server"
export DOTNET_HOST_TRACE=1 DOTNET_HOST_TRACEFILE="$state/server-host.trace"
if [[ ${HONUA_E2E_MIGRATION_PREFLIGHT:-0} = 1 && ! -f "$pid_file" ]]; then
    if ! HONUA_MIGRATE_ONLY=true ./Honua.Server </dev/null > "$state/migration.log" 2>&1; then
        echo "::error::The fixture database migration failed" >&2
        report_startup_diagnostics
        exit 1
    fi
    if ! psql -X -w -h 127.0.0.1 -U postgres -d honua -Atqc \
        "SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'honua')" | grep -qx t; then
        echo "::error::The migration exited without creating the honua schema" >&2
        report_startup_diagnostics
        exit 1
    fi
fi
# Keep the fixture running across the restart helper's exit, without requiring nohup.
(
    trap '' HUP
    exec ./Honua.Server
) </dev/null > "$state/server.log" 2>&1 &
server_pid=$!
echo "$server_pid" > "$pid_file"
for ((attempt = 0; attempt < 90; ++attempt)); do
    if curl --fail --silent --max-time 2 "$base_url/healthz/ready" >/dev/null; then
        exit 0
    fi
    if ! kill -0 "$server_pid" 2>/dev/null; then
        server_status=0
        wait "$server_pid" || server_status=$?
        echo "::error::The fixture server exited before becoming ready (status $server_status)" >&2
        report_startup_diagnostics
        exit 1
    fi
    sleep 2
done
echo "::error::Timed out waiting for the fixture server" >&2
ps -o pid,ppid,state,comm -p "$server_pid" >&2 || true
curl --silent --max-time 2 --output /dev/null --dump-header "$state/readiness.headers" \
    --write-out 'Fixture readiness HTTP %{http_code}\n' "$base_url/healthz/ready" >&2 || true
if [[ -s "$state/readiness.headers" ]]; then
    awk 'tolower($1) == "x-honua-readiness-reason:" && $2 ~ /^[a-z-]+\r?$/ { print $1, $2 }' "$state/readiness.headers" >&2
fi
report_startup_diagnostics
exit 1
RESTART
chmod 700 "$HONUA_E2E_RESTART_SERVER_COMMAND"
"$HONUA_E2E_RESTART_SERVER_COMMAND"
"$workspace/scripts/e2e/publish-field-runtime-fixture.sh" "$candidate" "$working_dir/admin-key"

echo "Waiting for the prebooted simulator $device"
run_bounded 540 xcrun simctl bootstatus "$device"
build_path=src/Honua.Field.App/bin/Debug/net11.0-ios/iossimulator-arm64
previous_app=$(find "$workspace/field-previous/$build_path" -maxdepth 1 -name '*.app' -type d)
test -d "$previous_app"
app=$(find "$workspace/$build_path" -maxdepth 1 -name '*.app' -type d)
test -d "$app"
# The Apple workload links simulator entitlements into the executable.
# The ad-hoc code signature does not carry these simulator entitlements.
executable=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$app/Info.plist")
bundle_id=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$app/Info.plist")
xcrun otool -s __TEXT __entitlements "$app/$executable" > "$working_dir/simulator-entitlements.txt"
python3 - "$working_dir/simulator-entitlements.txt" "$bundle_id" <<'PYTHON'
import pathlib
import plistlib
import re
import sys

dump = pathlib.Path(sys.argv[1]).read_text()
rows = re.findall(r"^[0-9a-fA-F]+[ \t]+([0-9a-fA-F \t]+)$", dump, re.MULTILINE)
try:
    # otool emits little-endian ARM instructions as 32-bit hex words and
    # prints any trailing bytes individually.
    contents = bytearray()
    for word in " ".join(rows).split():
        chunk = bytes.fromhex(word)
        contents.extend(chunk[::-1] if len(chunk) == 4 else chunk)
    entitlements = plistlib.loads(contents.rstrip(b"\0"))
except ValueError as error:
    sys.exit(f"Could not read embedded simulator entitlements: {error}")
if sys.argv[2] not in entitlements.get("keychain-access-groups", []):
    sys.exit(f"Missing simulator Keychain access group for {sys.argv[2]}")
print(f"Verified embedded simulator Keychain access group for {sys.argv[2]}")
PYTHON
echo "Installing previous Field build on $device ($(du -sh "$previous_app" | cut -f1) uncompressed)"
test "$bundle_id" = "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$previous_app/Info.plist")"
run_bounded 480 xcrun simctl install "$device" "$previous_app"
export HONUA_E2E_IOS_UPGRADE_APP="$app"
export HONUA_E2E_IOS_PREVIOUS_REVISION
HONUA_E2E_IOS_PREVIOUS_REVISION=${HONUA_E2E_IOS_PREVIOUS_REVISION:-$(git -C "$workspace/field-previous" rev-parse HEAD)}
echo "App install complete. Starting published form journey."

if ! run_bounded 2100 bash "$workspace/scripts/e2e/run-published-ios.sh" \
    "$device" "$HONUA_E2E_WORKER_KEY_FILE" "$working_dir/admin-key"; then
    if [[ ! -s "$report_dir/failure.png" ]]; then
        run_bounded 60 xcrun simctl io "$device" screenshot "$report_dir/failure.png" || true
    fi
    exit 1
fi
