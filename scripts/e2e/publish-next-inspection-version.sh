#!/usr/bin/env bash
# Publish a changed fixture while a worker has a draft pinned to the old version.
set -euo pipefail
port=${1:?Missing local fixture server port}
previous=${2:?Missing captured form version}
admin_key=${3:?Missing admin key file}
base="http://127.0.0.1:$port/api/v1/admin/forms/field_runtime_inspection/versions"
umask 077
working_dir=$(mktemp -d)
trap 'rm -r -- "$working_dir"' EXIT

printf 'X-API-Key: %s\n' "$(cat "$admin_key")" > "$working_dir/admin-header"
request=(curl --fail --silent --show-error --max-time 30 -H @"$working_dir/admin-header")
"${request[@]}" -X POST "$base/$previous/reopen" > "$working_dir/draft"
version=$(jq -er --argjson previous "$previous" '.version | select(. > $previous)' "$working_dir/draft")
printf 'If-Match: %s\n' "$(jq -er .etag "$working_dir/draft")" > "$working_dir/etag"
jq --arg title "Field Runtime Inspection, revision $version" '.package | .title = $title' "$working_dir/draft" > "$working_dir/package"
"${request[@]}" -X PUT -H @"$working_dir/etag" -H 'Content-Type: application/json' \
    --data-binary @"$working_dir/package" "$base/$version" >/dev/null
"${request[@]}" -X POST "$base/$version/publish" | jq -er --argjson version "$version" '.version | select(. == $version)'
