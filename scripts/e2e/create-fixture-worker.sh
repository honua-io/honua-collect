#!/usr/bin/env bash
# Create a worker credential scoped to one editable layer in a disposable fixture.
set -euo pipefail

if [[ $# -ne 4 ]]; then
    echo "Usage: $0 BASE_URL ADMIN_KEY_FILE SERVICE/LAYER WORKER_KEY_FILE" >&2
    exit 2
fi
base=${1%/}
umask 077
work=$(mktemp -d)
trap 'rm -r -- "$work"' EXIT
printf 'X-API-Key: %s\n' "$(cat "$2")" > "$work/admin-header"
jq -n --arg scope "$3" '{name:"Field fixture worker",permissions:[("read:" + $scope),("write:" + $scope)]}' > "$work/request"
curl --fail --silent --show-error -H @"$work/admin-header" -H 'Content-Type: application/json' \
    --data-binary @"$work/request" "$base/api/v1/admin/api-keys" > "$work/response"
jq -er '.data.key | select(type == "string" and length > 0)' "$work/response" > "$work/key"
install -m 600 "$work/key" "$4"
