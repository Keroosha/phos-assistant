#!/usr/bin/env bash
# Phos CI gate: build + formatter + linter + tests + coverage thresholds.
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet tool restore
dotnet restore Phos.sln
dotnet build Phos.sln -c Release --no-restore
dotnet fantomas . --check
dotnet dotnet-fsharplint lint Phos.sln --lint-config .fsharplint.json
dotnet test Phos.sln -c Release --no-build --collect:"XPlat Code Coverage"
python3 scripts/coverage-gate.py tests/*/TestResults
