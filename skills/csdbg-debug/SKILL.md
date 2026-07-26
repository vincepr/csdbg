---
name: csdbg-debug
description: Debug .NET runtime failures with the headless csdbg MCP server.
allowed-tools: >-
  get_status start_debug attach_debug stop_debug add_breakpoint
  remove_breakpoint set_exception_breakpoints continue_execution
  wait_for_stop pause_execution step_over step_into step_out get_threads
  get_call_stack get_scopes get_variables evaluate_expression
  get_exception_info
---

# Debug .NET with csdbg

Use csdbg to establish runtime evidence before changing code. Tool names may be
namespaced by the MCP client.

Read [setup.md](references/setup.md) only for installation or missing
prerequisites.
Read [diagnostics.md](references/diagnostics.md) only for large collections,
EF/Npgsql, or hidden HTTP failure details.

## Workflow

1. Call `get_status`. If the backend is unavailable, report the returned
   remediation instead of attempting a session.
2. Ensure the target is built from the current source with symbols. Resolve the
   DLL or executable for `start_debug`, or the managed PID for `attach_debug`.
   For a test runner, attach to its managed testhost rather than the parent CLI.
3. Add the earliest relevant breakpoint before launch or attach. Use
   `stopAtEntry` when no reliable source location is known. Configure exception
   breakpoints only when exceptions are relevant.
4. Start or attach. If it is running, call `wait_for_stop`; inspect only while
   stopped.
5. Inspect from broad to narrow:
   `get_threads` -> `get_call_stack` -> `get_scopes` -> `get_variables`.
   Follow `variablesReference` values to expand nested data.
6. Use `step_over`, `step_into`, `step_out`, or `continue_execution` to locate
   the first point where observed behavior diverges from expected behavior.
   Use `pause_execution` only when the target is running freely.
7. On an exception stop, call `get_exception_info`. Use
   `evaluate_expression` for focused hypotheses, preferably against an explicit
   `frameId`.
8. Fix the root cause, rebuild, reset shifted breakpoints, and reproduce the
   same path to verify the change.
9. Call `stop_debug` when finished. An attached target is disconnected without
   being intentionally terminated.

For a hit count, count matching breakpoint stops and continue until termination.

## Constraints

- Re-fetch frames, scopes, and variable references after execution resumes;
  stopped-state DAP identifiers may expire.
- Keep one active session per csdbg server.
- Prefer read-only expressions. Set `unsafe=true` only with explicit user
  authorization because evaluation can execute code or mutate target state.
- Report the root cause with observed locations and values; distinguish evidence
  from inference.
