#!/usr/bin/env bash
set -euo pipefail

project="banking-integration-${USER:-local}"
started=0
cleanup() { if [[ "$started" == "1" ]]; then docker compose -p "$project" down -v; fi; }
trap cleanup EXIT

if nc -z localhost 11433 && nc -z localhost 5672; then
  echo "Using SQL Server and RabbitMQ already listening on the configured lab ports."
elif ! nc -z localhost 11433 && ! nc -z localhost 5672; then
  docker compose -p "$project" up -d --wait sql rabbit
  started=1
else
  echo "SQL Server and RabbitMQ must either both be running or both have free lab ports." >&2
  exit 1
fi
BANKING_RUN_EXTERNAL_INTEGRATION=1 dotnet test Banking.slnx \
  --filter Category=ExternalIntegration
