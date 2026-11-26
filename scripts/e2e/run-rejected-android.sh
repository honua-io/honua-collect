#!/usr/bin/env bash
# Run only against a disposable published Android fixture database.
set -euo pipefail

if [[ $# -lt 2 || $# -gt 3 ]]; then
    echo "Usage: $0 ANDROID_SERIAL WORKER_KEY_FILE [READBACK_KEY_FILE]" >&2
    exit 2
fi

: "${HONUA_E2E_TEST_POSTGIS_CONTAINER:?Specify the disposable fixture PostGIS container}"
: "${HONUA_E2E_RESTART_SERVER_COMMAND:?Specify the executable that restarts the fixture server and waits for readiness}"
script_dir=$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
db=(docker exec -i "$HONUA_E2E_TEST_POSTGIS_CONTAINER" psql -XAtq -v ON_ERROR_STOP=1 -U postgres -d honua)
filter="layer_id = 68950 AND field_name = 'name'"
original=$("${db[@]}" -c "SELECT coalesce(max_length::text, 'NULL') FROM honua.layer_fields WHERE $filter")
if [[ ! $original =~ ^([0-9]+|NULL)$ ]]; then
    echo "Fixture layer name metadata is missing." >&2
    exit 1
fi
restore() {
    "${db[@]}" -c "UPDATE honua.layer_fields SET max_length = $original WHERE $filter"
    "$HONUA_E2E_RESTART_SERVER_COMMAND"
}
trap restore EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
"${db[@]}" -c "UPDATE honua.layer_fields SET max_length = 16 WHERE $filter"
"$HONUA_E2E_RESTART_SERVER_COMMAND"
started=$("${db[@]}" -c 'SELECT clock_timestamp()')
export HONUA_E2E_CORRECTED_NAME="Fix$(date +%s)"
"$script_dir/run-published-android.sh" "$@"

receipts=$("${db[@]}" -v started="$started" <<'SQL'
SELECT coalesce(jsonb_agg(to_jsonb(receipt) ORDER BY created_at), '[]')
FROM (
    SELECT submission_id, status, form_version, created_at,
           idempotency_key IS NOT NULL AS has_key,
           dense_rank() OVER (ORDER BY idempotency_key) AS key_rank,
           validation_json AS validation
    FROM honua.form_submissions
    WHERE form_id = 'field_runtime_inspection' AND created_at >= :'started'::timestamptz
) receipt;
SQL
)
if ! jq -e '
    . as $receipts | length == 2
    and $receipts[0].status == "rejected"
    and $receipts[1].status == "accepted"
    and $receipts[0].form_version == $receipts[1].form_version
    and $receipts[0].has_key and $receipts[1].has_key
    and $receipts[0].submission_id != $receipts[1].submission_id
    and $receipts[0].key_rank != $receipts[1].key_rank
    and any($receipts[0].validation[]; .code == "fieldValueTooLong" and .fieldId == "name")
' <<< "$receipts" >/dev/null; then
    echo "Expected one server rejection and one corrected acceptance on the same form version, with distinct submission IDs and idempotency keys." >&2
    exit 1
fi
if [[ -n ${HONUA_E2E_REPORT_DIR:-} ]]; then
    jq '[.[] | {submission_id, status, form_version, validation_codes: [.validation[]?.code]}]' \
        <<< "$receipts" > "$HONUA_E2E_REPORT_DIR/correction-ledger.json"
fi
echo "Verified one server rejection and one corrected acceptance on the pinned version with distinct submission IDs and idempotency keys."
