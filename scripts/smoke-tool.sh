#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
temp_root="$(mktemp -d)"
trap 'rm -rf "$temp_root"' EXIT

package_dir="$temp_root/packages"
tool_dir="$temp_root/tool"
mkdir -p "$package_dir" "$tool_dir" "$temp_root/home"

dotnet pack "$repo_root/src/Csdbg.Mcp/Csdbg.Mcp.csproj" \
  -c Release \
  --no-restore \
  -o "$package_dir"

expected="Usage: csdbg [--check | --install-netcoredbg | --help]"
local_actual="$(dotnet run \
  --project "$repo_root/src/Csdbg.Mcp/Csdbg.Mcp.csproj" \
  -c Release \
  --no-build \
  -- \
  --help)"
if [[ "$local_actual" != "$expected" ]]; then
  printf 'Unexpected local help output:\n%s\n' "$local_actual" >&2
  exit 1
fi

DOTNET_CLI_HOME="$temp_root/home" dotnet tool install csdbg-mcp \
  --tool-path "$tool_dir" \
  --add-source "$package_dir" \
  --ignore-failed-sources \
  --version 0.1.0

actual="$($tool_dir/csdbg --help)"
if [[ "$actual" != "$expected" ]]; then
  printf 'Unexpected help output:\n%s\n' "$actual" >&2
  exit 1
fi

printf 'Global tool smoke test passed.\n'
