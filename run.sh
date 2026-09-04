#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

MODE="${1:-Debug}"
shift || true

dotnet run --project RKCore.vbproj -c "$MODE" -- "$@"
