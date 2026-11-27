#!/usr/bin/env bash
# Activate the editable feature and attachment fixture in a disposable Server database.
set -euo pipefail

if [[ $# -ne 4 ]]; then
    echo "Usage: $0 SERVER_CHECKOUT POSTGIS_CONTAINER DB_USER DATABASE" >&2
    exit 2
fi
seed_dir=$1/tests/seed
db=(docker exec -i "$2" psql -X -v ON_ERROR_STOP=1 -U "$3" -d "$4")

"${db[@]}" < "$seed_dir/mobile-offline-demo-v1.sql"
sed -n '/^CREATE OR REPLACE FUNCTION honua\.seed_metadata_v2_compat_snapshot()/,/^\$\$;/p' \
    "$seed_dir/client-compat-v1.sql" | "${db[@]}"
"${db[@]}" -1 -f - <<'SQL'
UPDATE honua.services SET metadata = jsonb_set(metadata, '{accessPolicy,allowAnonymousWrite}', 'false')
WHERE service_name = 'mobile_offline_demo';
UPDATE honua.layers SET metadata = metadata || jsonb_build_object(
    'accessPolicy', COALESCE(metadata->'accessPolicy', '{}'::jsonb) || '{"allowAnonymous":true,"allowAnonymousWrite":false}'::jsonb,
    'annotations', COALESCE(metadata->'annotations', '{}'::jsonb) || '{"honua.io/attachments":"true"}'::jsonb)
WHERE layer_id = 68910;
SELECT honua.seed_metadata_v2_compat_snapshot();
SQL
