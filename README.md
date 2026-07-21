# csdbg

`csdbg` is an IDE-independent MCP debugger for agent-driven C#/.NET debugging.

[NuGet.org package](https://www.nuget.org/packages/Csdbg.Mcp)

## Install

Install the .NET 10 SDK, then install the global tool from NuGet.org:

```bash
dotnet tool install --global Csdbg.Mcp
csdbg --install-netcoredbg
csdbg --check
```

The backend installer supports Linux x64/arm64, macOS arm64, and Windows x64,
and selects the correct `netcoredbg` package automatically.

Configure an MCP client to launch `csdbg` over stdio:

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

## Use

The MCP server describes its tools and recommended next actions to the agent.
A typical debugging flow is:

1. Call `get_status`.
2. Add source breakpoints with `add_breakpoint`.
3. Launch a .NET DLL or executable with `start_debug`, or use `attach_debug`.
4. Inspect threads, call stacks, scopes, variables, and expressions while stopped.
5. Continue or step until the problem is understood.
6. Call `stop_debug` when finished.

Available MCP tools:

- Session: `get_status`, `start_debug`, `attach_debug`, `stop_debug`
- Breakpoints: `add_breakpoint`, `remove_breakpoint`, `set_exception_breakpoints`
- Execution: `continue_execution`, `pause_execution`, `step_over`, `step_into`, `step_out`
- Inspection: `get_threads`, `get_call_stack`, `get_scopes`, `get_variables`, `evaluate_expression`, `get_exception_info`

## Lifecycle

The MCP client owns the `csdbg` process. Running `csdbg` directly starts the same
stdio server, writes one startup message to stderr, and waits for MCP input;
stdout is reserved for protocol messages.

The server has no fixed idle timeout and starts `netcoredbg` only when a debug
session is requested. Closing the MCP client or its stdio connection stops the
server and cleans up the active session. Ctrl+C and SIGTERM also perform graceful
cleanup. `stop_debug` stops the debuggee and `netcoredbg` without stopping the
MCP server.

## Documentation

See the [development and design guide](https://github.com/vincepr/csdbg/blob/main/docs/development.md)
for local builds, tests, publishing, architecture, supported targets, and project
scope.

## Changelog

### 0.2.1 - 2026-07-21

- Added direct package discovery and automated GitHub Releases and Packages mirroring.

### 0.2.0 - 2026-07-21

- Improved MCP stdio startup, shutdown, CLI guidance, and agent initialization instructions.

### 0.1.0 - 2026-07-21

- Initial .NET tool and MCP server release with launch and attach debugging, breakpoints, execution control, inspection, expression evaluation, and exception handling.
