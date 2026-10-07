#!/usr/bin/env bash
# Applies the Prism EF Core migrations using a self-contained migration bundle.
#
#   PRISM_DB_CONNECTION  required  SQL Server connection string for the Prism database
#   PRISM_EFBUNDLE       optional  path to the bundle (default: ./efbundle)
#
# Extra arguments are passed to the bundle, e.g. a target migration name.
set -euo pipefail

: "${PRISM_DB_CONNECTION:?PRISM_DB_CONNECTION must be set}"
bundle="${PRISM_EFBUNDLE:-./efbundle}"

if [[ ! -x "$bundle" ]]; then
  echo "Migration bundle not found or not executable: $bundle" >&2
  exit 1
fi

exec "$bundle" --connection "$PRISM_DB_CONNECTION" "$@"
