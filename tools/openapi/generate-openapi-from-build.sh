#!/usr/bin/env bash

# Compatibility entry point. The offline generator exports both current .NET
# contracts and their WebAdmin types before publishing any generated artifacts.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo 'generate-openapi-from-build.sh is deprecated; use npm run generate:offline in tools/openapi.' >&2
cd "$SCRIPT_DIR"
exec npm run generate:offline -- "$@"
