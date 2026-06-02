#!/usr/bin/env bash
docker rm -f honua-e2e-srv honua-e2e-pg >/dev/null 2>&1 || true
docker network rm honua-e2e >/dev/null 2>&1 || true
echo "e2e stack down"
