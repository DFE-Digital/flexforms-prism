#!/bin/sh
set -e

if command -v update-ca-certificates >/dev/null 2>&1; then
    update-ca-certificates || true
fi

# Azure Functions isolated worker host (see mcr.microsoft.com/azure-functions/dotnet-isolated).
exec /opt/startup/start_nonappservice.sh
