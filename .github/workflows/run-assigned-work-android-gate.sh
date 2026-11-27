#!/usr/bin/env bash
# Run only inside the pilot workflow's dedicated throwaway Android emulator.
set -euo pipefail
umask 077

state=$(mktemp -d "$RUNNER_TEMP/assigned-work.XXXXXX")
container="honua-$(basename "$state")"
server_pid=
cleanup() {
    if [[ -n $server_pid ]]; then
        kill "$server_pid" 2>/dev/null || true
        wait "$server_pid" 2>/dev/null || true
    fi
    docker rm -fv "$container" >/dev/null 2>&1 || true
    rm -r -- "$state"
}
trap cleanup EXIT

openssl rand -hex 24 > "$state/admin"
openssl rand -hex 32 > "$state/signing"
openssl rand -hex 24 > "$state/pfx-password"
openssl rand -hex 24 > "$state/db-password"
printf 'POSTGRES_DB=honua\nPOSTGRES_PASSWORD=%s\n' "$(cat "$state/db-password")" > "$state/db.env"
docker run --detach --name "$container" --publish 127.0.0.1::5432 \
    --env-file "$state/db.env" "${POSTGIS_IMAGE:?}" >/dev/null
db_port=$(docker port "$container" 5432/tcp)
db_port=${db_port##*:}
wait_for() {
    for ((attempt = 0; attempt < 90; ++attempt)); do
        if "$@"; then return; fi
        sleep 2
    done
    echo "Timed out waiting for $1" >&2
    return 1
}
db_ready() {
    docker exec "$container" pg_isready -h 127.0.0.1 -U postgres -d honua >/dev/null 2>&1
}
wait_for db_ready

# This temporary trust anchor only authenticates the isolated loopback fixture.
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=Honua assigned work test' \
    -addext 'subjectAltName=IP:127.0.0.1' -keyout "$state/tls.key" -out "$state/tls.crt" 2>/dev/null
openssl pkcs12 -export -inkey "$state/tls.key" -in "$state/tls.crt" -out "$state/tls.pfx" \
    -passout "file:$state/pfx-password"
master=$(openssl rand -base64 32)
salt=$(openssl rand -base64 16)
server_ready() {
    curl --silent --fail --cacert "$state/tls.crt" https://127.0.0.1:5191/healthz/ready >/dev/null
}
start_server() {
    (
    cd "$RUNNER_TEMP/assigned-work-server"
    ASPNETCORE_ENVIRONMENT=Development \
    Kestrel__Endpoints__Https__Url=https://127.0.0.1:5191 \
    Kestrel__Endpoints__Https__Certificate__Path="$state/tls.pfx" \
    Kestrel__Endpoints__Https__Certificate__Password="$(cat "$state/pfx-password")" \
    ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=$db_port;Database=honua;Username=postgres;Password=$(cat "$state/db-password")" \
    HONUA_ADMIN_PASSWORD="$(cat "$state/admin")" \
    Security__ConnectionEncryption__MasterKey="$master" \
    Security__ConnectionEncryption__Salt="$salt" \
    Database__MigrationSafety__ContractApplyPolicy=Auto \
    Authentication__PortalToken__Enabled=true \
    Authentication__PortalToken__RequireHttps=true \
    Authentication__PortalCredentialVerifier__UseOidc=true \
    Oidc__Enabled=true Oidc__Generic__Enabled=true \
    Oidc__Generic__Authority=https://fixture.invalid Oidc__Generic__ClientId=honua-fixture \
    Oidc__AdminAuthCallbackUri=https://127.0.0.1:5191/admin/auth/callback \
    Oidc__TokenValidation__SymmetricSigningKey="$(cat "$state/signing")" \
    Oidc__TokenValidation__ValidIssuers__0=https://fixture.invalid \
    Oidc__TokenValidation__ValidAudiences__0=honua-fixture \
    Licensing__DevGrantEdition=Pro Geocoding__Enabled=false \
        exec ./Honua.Server
    ) > "$state/server.log" 2>&1 &
    server_pid=$!
    wait_for server_ready
}
printf "%s\n" initial-server >> "$RUNNER_TEMP/assigned-work-stage.txt"
start_server
db=(docker exec -i "$container" psql -X -v ON_ERROR_STOP=1 -U postgres -d honua)
"${db[@]}" < scripts/e2e/fixtures/field-runtime-inspection.sql >/dev/null
sed -n '/^CREATE OR REPLACE FUNCTION honua\.seed_metadata_v2_compat_snapshot()/,/^\$\$;/p' \
    server-candidate/tests/seed/base-schema.sql | "${db[@]}" >/dev/null
"${db[@]}" -1 -f - < scripts/e2e/fixtures/activate-field-runtime-metadata.sql >/dev/null
printf "%s\n" restarting-with-seed >> "$RUNNER_TEMP/assigned-work-stage.txt"
kill "$server_pid"
wait "$server_pid" || true
server_pid=
start_server
printf "%s\n" importing-assigned-work-form >> "$RUNNER_TEMP/assigned-work-stage.txt"
printf 'X-API-Key: %s\n' "$(cat "$state/admin")" > "$state/admin-header"
python3 scripts/e2e/fixtures/write-field-runtime-workbook.py --assigned-work "$state/inspection.xlsx"
api=(curl --fail --silent --show-error --cacert "$state/tls.crt" -H @"$state/admin-header")
base=https://127.0.0.1:5191/api/v1/admin/forms
"${api[@]}" -F "file=@$state/inspection.xlsx" "$base/import?serviceId=field_inspections&layerId=68950" > "$state/draft.json"
version=$(jq -er '.version | select(type == "number" and . > 0)' "$state/draft.json")
printf "%s\n" publishing-assigned-work-form >> "$RUNNER_TEMP/assigned-work-stage.txt"
"${api[@]}" -X POST "$base/field_runtime_inspection/versions/$version/publish" > /dev/null
printf "%s\n" seeding-assigned-work-task >> "$RUNNER_TEMP/assigned-work-stage.txt"
python3 scripts/e2e/fixtures/seed-assigned-work.py "$state" > "$state/task.json"

printf "%s\n" provisioning-device >> "$RUNNER_TEMP/assigned-work-stage.txt"
bash scripts/e2e/assigned-work-input/build.sh "$state/input"
serial=$(adb get-serialno)
device=(adb -s "$serial")
[[ $("${device[@]}" shell getprop ro.kernel.qemu) == 1 ]]
"${device[@]}" root >/dev/null
"${device[@]}" wait-for-device
hash=$(openssl x509 -subject_hash_old -noout -in "$state/tls.crt")
"${device[@]}" push "$state/tls.crt" "/data/local/tmp/$hash.0" >/dev/null
"${device[@]}" shell "
    set -e
    certs=/system/etc/security/cacerts
    cp -a \$certs /data/local/tmp/assigned-work-cacerts
    mount -t tmpfs tmpfs \$certs
    cp /data/local/tmp/assigned-work-cacerts/* \$certs/
    cp /data/local/tmp/$hash.0 \$certs/$hash.0
    chown 0:0 \$certs/*
    chmod 755 \$certs
    chmod 644 \$certs/*
    restorecon -R \$certs
    rm -r /data/local/tmp/assigned-work-cacerts /data/local/tmp/$hash.0
"
printf "%s\n" sign-in-and-cold-relaunch >> "$RUNNER_TEMP/assigned-work-stage.txt"
python3 scripts/e2e/run-assigned-work-android.py --serial "$serial" \
    --apk "$RUNNER_TEMP/assigned-work-apk/io.honua.collect-Signed.apk" --input-apk "$state/input/input.apk" \
    --url https://127.0.0.1:5191 --ca "$state/tls.crt" --tls-cert "$state/tls.crt" --tls-key "$state/tls.key" \
    --service "$(jq -r .serviceId "$state/task.json")" --task "$(jq -r .taskId "$state/task.json")" \
    --assignee "$(jq -r .assignee "$state/task.json")" --credentials "$state/worker.json" --report "$state/report.json"
printf "%s\n" final-readback >> "$RUNNER_TEMP/assigned-work-stage.txt"
jq --arg server "$(git -C server-candidate rev-parse HEAD)" \
   --arg archive "${PILOT_APK_ARTIFACT_DIGEST:?}" \
   --arg artifact "${PILOT_APK_ARTIFACT_ID:?}" \
   '. + {serverRevision: $server, apkArtifactArchiveSha256: $archive, apkArtifactId: $artifact}' "$state/report.json" \
   > "$RUNNER_TEMP/assigned-work-readback.json"
