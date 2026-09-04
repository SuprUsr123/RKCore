#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

MODE="${1:-Debug}"

dotnet build RKCore.vbproj -c "$MODE"
