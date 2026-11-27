#!/usr/bin/env bash
# Brings up an isolated Honua server + PostGIS with the editable
# mobile_offline_demo/68910 feature layer, for the Honua Field e2e suite.
# Reuses a prebuilt server image (HONUA_SERVER_IMAGE) to avoid a long build.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

if [ -z "${HONUA_SERVER_IMAGE:-}" ]; then
  echo "error: HONUA_SERVER_IMAGE is required (for example: HONUA_SERVER_IMAGE=ghcr.io/honua-io/honua-server:<tag> scripts/e2e/up.sh)." >&2
  exit 1
fi
IMAGE="${HONUA_SERVER_IMAGE}"

SERVER_REPO="${HONUA_SERVER_REPO:-${REPO_ROOT}/../honua-server}"
SNAPSHOT_SQL="${SERVER_REPO}/tests/seed/client-compat-v1.sql"
SEED_SQL="${SERVER_REPO}/tests/seed/mobile-offline-demo-v1.sql"
if [ ! -f "${SNAPSHOT_SQL}" ] || [ ! -f "${SEED_SQL}" ]; then
  echo "error: required honua-server seed files not found under '${SERVER_REPO}' (expected tests/seed/client-compat-v1.sql and tests/seed/mobile-offline-demo-v1.sql). Set HONUA_SERVER_REPO to a local honua-server checkout." >&2
  exit 1
fi
PGPORT="${PGPORT:-55432}"; HTTPPORT="${HTTPPORT:-18080}"
# Admin/API key for the ephemeral e2e server. No literal default is committed:
# supply HONUA_E2E_APIKEY, or read the generated key from the private file reported below.
ADMIN_PW="${HONUA_E2E_APIKEY:-$(head -c 18 /dev/urandom | base64 | tr -dc 'A-Za-z0-9')}"
# Ephemeral container DB password — random per run, never reused or shipped.
PGPW="${HONUA_E2E_PGPASSWORD:-$(head -c 18 /dev/urandom | base64 | tr -dc 'A-Za-z0-9')}"

if docker container inspect honua-e2e-pg >/dev/null 2>&1 || docker container inspect honua-e2e-srv >/dev/null 2>&1; then
  echo "The honua-e2e containers already exist. Use the existing fixture or run scripts/e2e/down.sh before starting a new one." >&2
  exit 1
fi
docker network create honua-e2e 2>/dev/null || true

docker run -d --name honua-e2e-pg --network honua-e2e \
  -e POSTGRES_DB=honua_dev -e POSTGRES_USER=honua_user -e POSTGRES_PASSWORD=$PGPW \
  -p "127.0.0.1:${PGPORT}:5432" "${HONUA_E2E_POSTGIS_IMAGE:-postgis/postgis:18-3.6}" >/dev/null
until docker exec honua-e2e-pg pg_isready -U honua_user -d honua_dev >/dev/null 2>&1; do sleep 2; done

MK=$(head -c 32 /dev/urandom | base64); SALT=$(head -c 16 /dev/urandom | base64)
run_srv() {
  docker rm -f honua-e2e-srv >/dev/null 2>&1 || true
  docker run -d --name honua-e2e-srv --network honua-e2e \
    -e Licensing__DevGrantEdition=Pro -e ASPNETCORE_ENVIRONMENT=Development \
    -e ConnectionStrings__DefaultConnection="Host=honua-e2e-pg;Database=honua_dev;Username=honua_user;Password=$PGPW" \
    -e Kestrel__Endpoints__Http__Url="http://+:8080" -e Kestrel__Endpoints__Http__Protocols="Http1" \
    -e Security__ConnectionEncryption__MasterKey="$MK" -e Security__ConnectionEncryption__Salt="$SALT" \
    -e HONUA_ADMIN_PASSWORD="$ADMIN_PW" \
    -e Authentication__PortalToken__Enabled="true" \
    -e Authentication__PortalToken__RequireHttps="false" \
    -e Authentication__PortalCredentialVerifier__FallBackToAdminVerifier="true" \
    -e Geocoding__Enabled="false" \
    -e Geocoding__Nominatim__BaseUrl="https://nominatim.openstreetmap.org/" \
    -p "127.0.0.1:${HTTPPORT}:8080" "$IMAGE" >/dev/null
}
wait_ready() { for _ in $(seq 1 40); do sleep 3; [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:${HTTPPORT}/healthz/ready)" = "200" ] && return 0; done; return 1; }

run_srv
wait_ready

"${SCRIPT_DIR}/seed-feature-sync.sh" "$SERVER_REPO" honua-e2e-pg honua_user honua_dev
run_srv
wait_ready

if [[ -z ${HONUA_E2E_APIKEY:-} ]]; then
  key_file=$(mktemp)
  printf '%s\n' "$ADMIN_PW" > "$key_file"
  echo "Generated HONUA_E2E_APIKEY in $key_file. Remove this file after the test."
fi
echo "Ready at http://localhost:${HTTPPORT} (service mobile_offline_demo / layer 68910)."
