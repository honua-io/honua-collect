#!/usr/bin/env bash
set -euo pipefail
npm ci --ignore-scripts --prefix scripts/shell-spike
npm run assets --prefix scripts/shell-spike
dotnet build src/Honua.Collect.Spike.Web/Honua.Collect.Spike.Web.csproj -c Release -m:4 -p:UseSharedCompilation=false
