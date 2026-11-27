#!/usr/bin/env bash
# Apply or restore test-only restrictions for the disposable inspection layer.
set -euo pipefail
limit=${1:?Missing fixture name length}
[[ $limit =~ ^([0-9]+|NULL)$ ]]
script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
container=${HONUA_E2E_TEST_POSTGIS_CONTAINER:?Specify the disposable fixture PostGIS container}
db=(docker exec -i "$container" psql -XAtq -v ON_ERROR_STOP=1 -U postgres -d honua)
{
    cat <<'SQL'
UPDATE honua.layer_fields
SET max_length = NULLIF(:'limit', 'NULL')::integer
WHERE layer_id = 68950 AND field_name = 'name';
SQL
    cat "$script_dir/fixtures/activate-field-runtime-metadata.sql"
} | "${db[@]}" -1 -v limit="$limit" -f - >/dev/null
"${HONUA_E2E_RESTART_SERVER_COMMAND:?Specify the executable that restarts the fixture server}"

if [[ $# == 2 ]]; then
    base="http://127.0.0.1:${HONUA_E2E_HTTP_PORT:-5189}"
    if ! printf 'X-API-Key: %s\n' "$(cat "$2")" |
        curl --fail --silent --show-error -H @- "$base/rest/services/field_inspections/FeatureServer/68950?f=json" |
        jq -e --argjson limit "$limit" 'any(.fields[]; .name == "name" and .length == $limit)' >/dev/null; then
        echo "Server layer metadata does not expose the temporary $limit-character name limit." >&2
        exit 1
    fi
fi
