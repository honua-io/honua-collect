#!/usr/bin/env bash
# Execute only through remote-run on the authorized build host.
set -euo pipefail
bash scripts/shell-spike/validate.sh
mkdir -p artifacts/shell-spike
fixture=$(mktemp -d)
export COLLECT_SPIKE_DATA="$fixture"
export COLLECT_SPIKE_URL=http://127.0.0.1:5280
export ASPNETCORE_ENVIRONMENT=Development
dotnet src/Honua.Collect.Spike.Web/bin/Release/net10.0/Honua.Collect.Spike.Web.dll --urls "$COLLECT_SPIKE_URL" >artifacts/shell-spike/browser-host.log 2>&1 &
host_pid=$!
cleanup() { kill "$host_pid" 2>/dev/null || true; wait "$host_pid" 2>/dev/null || true; rm -rf "$fixture"; }
trap cleanup EXIT
for attempt in {1..60}; do
    if curl -fsS "$COLLECT_SPIKE_URL" >/dev/null; then break; fi
    if ! kill -0 "$host_pid" 2>/dev/null; then cat artifacts/shell-spike/browser-host.log; exit 1; fi
    sleep 1
done
cd scripts/shell-spike
npx --no-install playwright install chromium
npx --no-install playwright test inspection.spec.mjs --workers=1 --reporter=line
