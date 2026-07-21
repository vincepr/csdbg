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

local_actual="$(dotnet run \
  --project "$repo_root/src/Csdbg.Mcp/Csdbg.Mcp.csproj" \
  -c Release \
  --no-build \
  -- \
  --help)"
rg -F 'Usage: csdbg [--check | --install-netcoredbg | --version | --help]' <<<"$local_actual" >/dev/null
rg -F 'dotnet tool install --global Csdbg.Mcp' <<<"$local_actual" >/dev/null
rg -F '"command": "csdbg"' <<<"$local_actual" >/dev/null

DOTNET_CLI_HOME="$temp_root/home" dotnet tool install Csdbg.Mcp \
  --tool-path "$tool_dir" \
  --add-source "$package_dir" \
  --ignore-failed-sources \
  --version 0.2.1

actual="$($tool_dir/csdbg --help)"
if [[ "$actual" != "$local_actual" ]]; then
  printf 'Unexpected help output:\n%s\n' "$actual" >&2
  exit 1
fi
[[ "$($tool_dir/csdbg --version)" == "csdbg 0.2.1" ]]

printf 'Global tool smoke test passed.\n'
