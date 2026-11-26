#!/usr/bin/env bash
# Use a migrated, disposable Honua server and its fixture database.
set -euo pipefail

if [[ $# -ne 2 ]]; then
    echo "Usage: $0 HONUA_SERVER_CHECKOUT ADMIN_PASSWORD_FILE" >&2
    exit 2
fi

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
server_seed=$1/tests/seed/base-schema.sql
admin_key_file=$2
port=${HONUA_E2E_HTTP_PORT:-5189}
base="http://127.0.0.1:$port"
container=${HONUA_E2E_PG_CONTAINER:-honua-field-xlsform-pg}
database=${HONUA_E2E_PG_DATABASE:-honua}
user=${HONUA_E2E_PG_USER:-postgres}
form=field_runtime_inspection

working_dir=$(mktemp -d)
trap 'rm -r -- "$working_dir"' EXIT
umask 077

db=(docker exec -i "$container" psql -X -v ON_ERROR_STOP=1 -U "$user" -d "$database")
"${db[@]}" < "$script_dir/fixtures/field-runtime-inspection.sql" >/dev/null
sed -n '/^CREATE OR REPLACE FUNCTION honua\.seed_metadata_v2_compat_snapshot()/,/^\$\$;/p' "$server_seed" | "${db[@]}" >/dev/null
"${db[@]}" -1 -f - < "$script_dir/fixtures/activate-field-runtime-metadata.sql" >/dev/null

if [[ -n ${HONUA_E2E_RESTART_SERVER_COMMAND:-} ]]; then
    "$HONUA_E2E_RESTART_SERVER_COMMAND"
elif [[ -n ${HONUA_E2E_RESTART_SERVER_CONTAINER:-} ]]; then
    docker restart "$HONUA_E2E_RESTART_SERVER_CONTAINER" >/dev/null
fi
for ((attempt = 0; attempt < 45; ++attempt)); do
    if curl --fail --silent "$base/healthz/ready" > /dev/null &&
        curl --fail --silent "$base/rest/services/field_inspections/FeatureServer/68950?f=json" |
        jq -e '.hasAttachments == true' > /dev/null; then
        break
    fi
    sleep 2
done
if [[ $attempt == 45 ]]; then
    echo "The fixture layer did not load. Restart Honua after seeding and rerun this script." >&2
    exit 1
fi

printf 'X-API-Key: %s\n' "$(cat "$admin_key_file")" > "$working_dir/admin-header"
if ! curl --fail --silent -H @"$working_dir/admin-header" "$base/api/v1/forms/$form" > "$working_dir/published"; then
    workbook="$working_dir/field-runtime-inspection.xlsx"
    python3 "$script_dir/fixtures/write-field-runtime-workbook.py" "$workbook"
    if ! curl --fail-with-body --silent --show-error -H @"$working_dir/admin-header" -F "file=@$workbook" \
        "$base/api/v1/admin/forms/import?serviceId=field_inspections&layerId=68950" > "$working_dir/draft"; then
        jq -c . "$working_dir/draft" >&2
        exit 1
    fi
elif ! jq -e '.package.offlinePolicy.enabled == true' "$working_dir/published" >/dev/null; then
    version=$(jq -er '.version' "$working_dir/published")
    curl --fail-with-body --silent --show-error -H @"$working_dir/admin-header" -X POST \
        "$base/api/v1/admin/forms/$form/versions/$version/reopen" > "$working_dir/draft"
fi
if [[ -s $working_dir/draft ]]; then
    version=$(jq -er '.version | select(type == "number" and . > 0)' "$working_dir/draft")
    printf 'If-Match: %s\n' "$(jq -er '.etag' "$working_dir/draft")" > "$working_dir/etag-header"
    jq -e '.package.offlinePolicy.enabled = true | .package' "$working_dir/draft" > "$working_dir/package"
    if ! curl --fail-with-body --silent --show-error -H @"$working_dir/admin-header" -H @"$working_dir/etag-header" \
        -H 'Content-Type: application/json' -X PUT --data-binary @"$working_dir/package" \
        "$base/api/v1/admin/forms/$form/versions/$version" > "$working_dir/updated"; then
        jq -c . "$working_dir/updated" >&2
        exit 1
    fi
    if ! curl --fail-with-body --silent --show-error -H @"$working_dir/admin-header" -X POST \
        "$base/api/v1/admin/forms/$form/versions/$version/publish" > "$working_dir/published"; then
        jq -c . "$working_dir/published" >&2
        exit 1
    fi
fi
version=$(jq -er 'select(.package.offlinePolicy.enabled == true) | .version | select(type == "number" and . > 0)' "$working_dir/published")
echo "Published $form version $version at $base."

# Only the disposable fixture worker key goes to the Android runner.
if [[ -n ${HONUA_E2E_WORKER_KEY_FILE:-} ]]; then
    if [[ ! -s $HONUA_E2E_WORKER_KEY_FILE ]]; then
        curl --fail --silent --show-error -H @"$working_dir/admin-header" -H 'Content-Type: application/json' \
            --data-binary '{"name":"Android fixture worker","permissions":["read:field_inspections/Field Inspections","write:field_inspections/Field Inspections"]}' \
            "$base/api/v1/admin/api-keys" > "$working_dir/worker-response"
        jq -er '.data.key | select(type == "string" and length > 0)' "$working_dir/worker-response" > "$working_dir/worker-key"
        install -m 600 "$working_dir/worker-key" "$HONUA_E2E_WORKER_KEY_FILE"
    fi
    printf 'X-API-Key: %s\n' "$(cat "$HONUA_E2E_WORKER_KEY_FILE")" > "$working_dir/worker-header"
    curl --fail --silent --show-error -H @"$working_dir/worker-header" "$base/api/v1/forms/$form/versions/$version" >/dev/null
fi
