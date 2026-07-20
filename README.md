# csdbg

`csdbg` is an IDE-independent debugger control plane for agent-driven C#/.NET debugging.

The first version is intentionally small:

- C#/.NET first.
- MCP server first.
- No IDE dependency.
- No interactive REPL.
- One in-process debug session per MCP server instance.
- Scriptable stdio JSON-RPC interface.

## Target

The tool itself targets `.NET 10`.

The intended debuggee matrix is:

- `.NET 10`
- `.NET 9`
- `.NET 8`

Linux is the first implementation target. Windows and macOS should stay viable by keeping paths, process handling, and DAP lifecycle behind small abstractions.

Session metadata is stored outside the repo:

- Linux: `$XDG_STATE_HOME/csdbg` or `~/.local/state/csdbg`
- macOS: `~/Library/Application Support/csdbg`
- Windows: `%LOCALAPPDATA%\csdbg`

Set `CSDBG_NETCOREDBG` to the full path of `netcoredbg`, or put `netcoredbg` on `PATH`.
The explicit setting takes precedence over the legacy `NETCOREDBG_PATH` setting and `PATH` discovery.

Check the local backend and installed .NET runtimes before starting the MCP server:

```bash
dotnet run --project src/Csdbg.Mcp/Csdbg.Mcp.csproj -- --check
```

The command prints one JSON result and exits nonzero when `netcoredbg` cannot run, no `Microsoft.NETCore.App` runtime is found, or a bounded DAP launch cannot stop inside a managed probe process.

Install the pinned `netcoredbg` release into the current user's data directory:

```bash
dotnet run --project src/Csdbg.Mcp/Csdbg.Mcp.csproj -- --install-netcoredbg
```

The installer supports Linux x64/arm64, macOS arm64, and Windows x64. The managed installation is discovered automatically.

## Initial Architecture

The repo starts with two projects:

- `Csdbg.Core`: backend detection, DAP transport, and debug session state.
- `Csdbg.Mcp`: minimal MCP stdio server for agent-driven debugging.

Build the MCP project directly:

```bash
dotnet build src/Csdbg.Mcp/Csdbg.Mcp.csproj
```

## Backend Plan

The first debugger backend should be `netcoredbg` in DAP mode.

The core debugger path is:

```text
agent -> MCP tools -> in-process debug session -> DAP client -> netcoredbg -> target process
```

The current implementation starts with the MCP session path. `start_debug` can launch a .NET program under `netcoredbg`, breakpoint tools manage source breakpoints, and `get_status` / `stop_debug` manage the session lifecycle.
Continue, pause, and stepping tools wait for the next stop, exit, or timeout and return updated session state.
Inspection tools expose threads, stack frames, scopes, variables, and cautious expression evaluation while the debuggee is stopped.

Run the MCP server directly:

```bash
dotnet run --project src/Csdbg.Mcp/Csdbg.Mcp.csproj
```

Current MCP tools:

- `get_status`
- `add_breakpoint`
- `remove_breakpoint`
- `start_debug`
- `attach_debug`
- `continue_execution`
- `pause_execution`
- `step_over`
- `step_into`
- `step_out`
- `get_threads`
- `get_call_stack`
- `get_scopes`
- `get_variables`
- `evaluate_expression`
- `set_exception_breakpoints`
- `get_exception_info`
- `stop_debug`

The MCP server owns one in-process debug session and talks to `netcoredbg` over DAP.
Successful tool content uses a consistent `state`, `data`, and `nextActions` envelope. Tool execution failures return structured MCP tool errors with stable codes such as `wrong_state`, `invalid_arguments`, and `backend_unavailable`.

## Safety Rules

Defaults should favor agent reliability:

- No REPL in v1.
- One in-process session per server instance.
- Structured MCP responses only.
- Evaluation is explicit; assignments, increment/decrement, and method calls require `unsafe=true`.
- Launch is the main debugging path; attach preserves the target when disconnecting.

## Current Scope

In scope for the first implementation:

- MCP stdio server.
- Launch under `netcoredbg`.
- Session status and stop.
- Breakpoint registration and sync.
- Verified breakpoint updates from `netcoredbg`.
- Waitable continue, pause, and stepping execution.
- Thread, stack, scope, variable, and expression inspection.
- Backend detection for `netcoredbg`.
- Integration debuggee project under `integration/`.

Out of scope for the first implementation:

- Rider or VS Code plugins.
- Rust.
- Remote debugging.
- Full diagnostics tooling.
- Documentation samples beyond this README.
