# Setup

## Requirements

- .NET 10 SDK to install and run `csdbg`.
- A supported host: Linux x64/arm64, macOS arm64, or Windows x64.
- Network access while installing the NuGet tool and `netcoredbg` backend.
- Matching debug symbols and current source for source-level breakpoints.
- Permission to inspect the target process when attaching.

`csdbg` debugs .NET 8, 9, and 10 programs and does not require an IDE.

## Install the .NET SDK

### Windows

```powershell
winget install Microsoft.DotNet.SDK.10
```

Alternatively, use [Microsoft's Windows installer][windows-dotnet].

### macOS

Install the .NET 10 **Arm64 SDK** with [Microsoft's macOS installer][mac-dotnet].

### Linux

Follow [Microsoft's instructions for the distribution][linux-dotnet]. For a
supported Ubuntu release:

```bash
sudo apt-get update
sudo apt-get install -y dotnet-sdk-10.0
```

Verify the SDK:

```bash
dotnet --version
```

## Install csdbg

```bash
dotnet tool install --global Csdbg.Mcp
csdbg --install-netcoredbg
csdbg --check
```

If `csdbg` is not found, add the global tool directory to `PATH`:

- Linux/macOS: `$HOME/.dotnet/tools`
- Windows: `%USERPROFILE%\.dotnet\tools`

Upgrade with:

```bash
dotnet tool update --global Csdbg.Mcp
csdbg --install-netcoredbg
csdbg --check
```

## Register the MCP server

For Codex:

```bash
codex mcp add csdbg -- csdbg
codex mcp get csdbg
```

Generic stdio configuration:

```json
{
  "mcpServers": {
    "csdbg": {
      "command": "csdbg",
      "args": []
    }
  }
}
```

Restart the MCP client after changing its configuration.

## Install this skill

Copy the complete `csdbg-debug` directory to a skills directory recognized by
the agent:

- Codex: `$CODEX_HOME/skills/csdbg-debug` or `~/.codex/skills/csdbg-debug`
- Cross-agent location: `~/.agents/skills/csdbg-debug`

Keep the `references` directory with the skill so this setup guide remains
available on demand.

[linux-dotnet]: https://learn.microsoft.com/dotnet/core/install/
[mac-dotnet]: https://learn.microsoft.com/dotnet/core/install/macos
[windows-dotnet]: https://learn.microsoft.com/dotnet/core/install/windows
