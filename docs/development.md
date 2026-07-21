# Development and Design

## Local Development

Build and run directly from the repository:

```bash
dotnet build src/Csdbg.Mcp/Csdbg.Mcp.csproj
dotnet run --project src/Csdbg.Mcp/Csdbg.Mcp.csproj
```

Backend setup and health checks work through the local project as well:

```bash
dotnet run --project src/Csdbg.Mcp/Csdbg.Mcp.csproj -- --install-netcoredbg
dotnet run --project src/Csdbg.Mcp/Csdbg.Mcp.csproj -- --check
```

Create and test the tool package locally:

```bash
dotnet pack src/Csdbg.Mcp/Csdbg.Mcp.csproj -c Release -o artifacts
dotnet tool install Csdbg.Mcp --tool-path artifacts/tool --add-source artifacts --version 0.2.1
artifacts/tool/csdbg --check
```

Run the full test suite before committing production changes:

```bash
dotnet test Csdbg.slnx --configuration Release
```

## Publishing

The `CI` workflow runs the full Release test suite with read-only permissions on
every pull request and push to `main`.

To publish, set `<Version>` in `src/Csdbg.Mcp/Csdbg.Mcp.csproj`, add the matching
README changelog entry, and merge the change to `main`. The `Publish .NET tool`
workflow runs only on `main` or by manual dispatch. It repeats the tests against
the exact release commit, packs the tool, obtains a short-lived NuGet.org
credential through trusted publishing, and publishes the package to NuGet.org.
Only after that succeeds, the workflow creates a versioned GitHub Release linking
to NuGet.org and mirrors the same package to GitHub Packages using
`GITHUB_TOKEN`. NuGet.org is the required release target; the GitHub surfaces are
best-effort and do not fail an otherwise successful release. Existing GitHub
Releases and package versions are skipped. After tests pass, an existing version
on NuGet.org skips every publication step so different archives cannot be
published under the same version across registries.

## Design

The project is intentionally small:

- C#/.NET first.
- MCP server first.
- No IDE dependency.
- No interactive REPL.
- One in-process debug session per MCP server instance.
- Scriptable stdio JSON-RPC interface.

The solution has two production projects:

- `Csdbg.Core`: backend detection, DAP transport, and debug session state.
- `Csdbg.Mcp`: MCP stdio server and .NET tool entry point.

The debugger path is:

```text
agent -> MCP tools -> in-process debug session -> DAP client -> netcoredbg -> target process
```

The MCP server owns one debug session and talks to `netcoredbg` over DAP.
Successful tool content uses a consistent `state`, `data`, and `nextActions`
envelope. Tool failures return structured MCP errors with stable codes such as
`wrong_state`, `invalid_arguments`, and `backend_unavailable`.

## Targets

The tool targets `.NET 10`. The intended debuggee matrix is `.NET 8`, `.NET 9`,
and `.NET 10`.

The backend installer supports:

- Linux x64 and arm64
- macOS arm64
- Windows x64

Paths, process handling, and the DAP lifecycle remain behind small abstractions
to preserve cross-platform behavior.

Session metadata is stored outside the repository:

- Linux: `$XDG_STATE_HOME/csdbg` or `~/.local/state/csdbg`
- macOS: `~/Library/Application Support/csdbg`
- Windows: `%LOCALAPPDATA%\csdbg`

Set `CSDBG_NETCOREDBG` to the full path of `netcoredbg`, or put `netcoredbg` on
`PATH`. The explicit setting takes precedence over the legacy
`NETCOREDBG_PATH` setting and `PATH` discovery.

The health check exits nonzero when `netcoredbg` cannot run, no
`Microsoft.NETCore.App` runtime is found, or a bounded DAP launch cannot stop
inside a managed probe process.

## Safety

Defaults favor agent reliability:

- No REPL.
- One in-process session per server instance.
- Structured MCP responses only.
- Evaluation is explicit; assignments, increment/decrement, and method calls require `unsafe=true`.
- Launch is the main debugging path; attach preserves the target when disconnecting.

## Scope

Current functionality includes launch and attach debugging, source and exception
breakpoints, execution control, threads, call stacks, scopes, variables,
expression evaluation, exception inspection, backend detection, and the
integration debuggee under `integration/`.

Rider and VS Code plugins, Rust, remote debugging, and full diagnostics tooling
are currently out of scope.
