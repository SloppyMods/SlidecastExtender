#!/usr/bin/env bash
# Linux: build core + run tests. (Plugin build is Windows-only: ./scripts/build-plugin.ps1)
set -euo pipefail
export PATH="${HOME}/.dotnet:${PATH}"
export DOTNET_ROOT="${HOME}/.dotnet"
cd "$(dirname "$0")/.."
dotnet build SlidecastExtender.slnx -c Release
dotnet test SlidecastExtender.slnx -c Release --no-build
