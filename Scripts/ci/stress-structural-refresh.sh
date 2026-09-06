#!/usr/bin/env bash
set -euo pipefail

repository_root="${1:?repository root is required}"
dotnet_executable="${2:?dotnet executable is required}"
iterations="${3:-200}"
workers="${4:-$(nproc)}"
if [[ -z "${4:-}" ]] && ((workers > 8)); then
  workers=8
fi

load_pids=()
cleanup() {
  if ((${#load_pids[@]} > 0)); then
    kill "${load_pids[@]}" 2>/dev/null || true
    wait "${load_pids[@]}" 2>/dev/null || true
  fi
}
trap cleanup EXIT

for _ in $(seq 1 "$workers"); do
  yes >/dev/null &
  load_pids+=("$!")
done

cd "$repository_root"
test_assembly="Tests/DevProjex.Tests.Terminal/bin/Release/net10.0/DevProjex.Tests.Terminal.dll"
test_method="DevProjex.Tests.Terminal.TerminalWorkspaceContractTests.StructuralRefreshBuildsOnlyPlansRequiredBySelectionEvolution"
for iteration in $(seq 1 "$iterations"); do
  if ! output=$(
    "$dotnet_executable" "$test_assembly" \
      -method "$test_method" \
      -noLogo \
      -noColor 2>&1
  ); then
    printf '%s\n' "$output"
    echo "FAILED iteration=$iteration workers=$workers"
    exit 1
  fi
  if ((iteration % 25 == 0)); then
    echo "passed=$iteration workers=$workers"
  fi
done

echo "passed=$iterations workers=$workers"
