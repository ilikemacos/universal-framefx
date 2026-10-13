#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export PATH="${HOME}/.dotnet:${PATH}"
cd "$ROOT/preset-ai-ref"
python3 gen_onnx_golden.py
cd "$ROOT"
dotnet test PresetAi.Tests/PresetAi.Tests.csproj -c Release --verbosity minimal
echo "Chat demo harness: PresetAi.Tests passed (includes scripted chat_demo conversation)."
