#!/usr/bin/env bash
set -Eeuo pipefail

CSDBG_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CSDBG_SERVER="$CSDBG_ROOT/src/Csdbg.Mcp/bin/Debug/net10.0/csdbg-mcp.dll"
CSDBG_PROJECT="$CSDBG_ROOT/integration/DebuggableProject/DebuggableProject.csproj"
CSDBG_PROGRAM="$CSDBG_ROOT/integration/DebuggableProject/bin/Debug/net10.0/DebuggableProject.dll"
CSDBG_SOURCE="$CSDBG_ROOT/integration/DebuggableProject/Program.cs"
CSDBG_DOTNET_HOME="${DOTNET_CLI_HOME:-/tmp/csdbg-dotnet-home}"
CSDBG_PACKAGES="${NUGET_PACKAGES:-/tmp/csdbg-nuget}"
CSDBG_RESPONSE_TIMEOUT="${CSDBG_RESPONSE_TIMEOUT:-10}"
CSDBG_SMOKE_TIMEOUT="${CSDBG_SMOKE_TIMEOUT:-90}"

command -v dotnet >/dev/null
command -v jq >/dev/null
command -v rg >/dev/null

DOTNET_CLI_HOME="$CSDBG_DOTNET_HOME" NUGET_PACKAGES="$CSDBG_PACKAGES" \
    dotnet build "$CSDBG_PROJECT" --nologo >/dev/null
DOTNET_CLI_HOME="$CSDBG_DOTNET_HOME" NUGET_PACKAGES="$CSDBG_PACKAGES" \
    dotnet build "$CSDBG_ROOT/src/Csdbg.Mcp/Csdbg.Mcp.csproj" --nologo >/dev/null
dotnet "$CSDBG_SERVER" --check | jq -e '.healthy and .debuggerCompatible' >/dev/null

coproc CSDBG_MCP { dotnet "$CSDBG_SERVER"; }
CSDBG_MCP_PROCESS="$CSDBG_MCP_PID"
exec {CSDBG_MCP_INPUT}>&"${CSDBG_MCP[1]}"
exec {CSDBG_MCP_OUTPUT}<&"${CSDBG_MCP[0]}"

cleanup() {
    if [[ -n "${CSDBG_WATCHDOG:-}" ]]; then
        kill "$CSDBG_WATCHDOG" 2>/dev/null || true
    fi
    if [[ -n "${CSDBG_ATTACH_PROCESS:-}" ]] && kill -0 "$CSDBG_ATTACH_PROCESS" 2>/dev/null; then
        kill "$CSDBG_ATTACH_PROCESS" 2>/dev/null || true
        wait "$CSDBG_ATTACH_PROCESS" 2>/dev/null || true
    fi
    exec {CSDBG_MCP_INPUT}>&- || true
    exec {CSDBG_MCP_OUTPUT}<&- || true
    if kill -0 "$CSDBG_MCP_PROCESS" 2>/dev/null; then
        kill "$CSDBG_MCP_PROCESS" 2>/dev/null || true
    fi
    wait "$CSDBG_MCP_PROCESS" 2>/dev/null || true
}
trap cleanup EXIT

report_error() {
    local status="$?"
    printf 'MCP smoke test failed near line %s\n' "${BASH_LINENO[0]}" >&2
    if [[ -n "${CSDBG_RESPONSE:-}" ]]; then
        printf '%s\n' "$CSDBG_RESPONSE" | jq . >&2 || true
    fi
    exit "$status"
}
trap report_error ERR

(
    sleep "$CSDBG_SMOKE_TIMEOUT"
    printf 'MCP smoke test exceeded %s seconds\n' "$CSDBG_SMOKE_TIMEOUT" >&2
    kill -TERM "$$"
) &
CSDBG_WATCHDOG="$!"

send_request() {
    local id="$1"
    local method="$2"
    local params="$3"

    jq -cn \
        --argjson id "$id" \
        --arg method "$method" \
        --argjson params "$params" \
        '{jsonrpc:"2.0", id:$id, method:$method, params:$params}' >&"$CSDBG_MCP_INPUT"
}

read_response() {
    IFS= read -r -t "$CSDBG_RESPONSE_TIMEOUT" CSDBG_RESPONSE <&"$CSDBG_MCP_OUTPUT"
}

call_tool() {
    local id="$1"
    local name="$2"
    local arguments="$3"

    send_request "$id" tools/call "$(jq -cn --arg name "$name" --argjson arguments "$arguments" '{name:$name, arguments:$arguments}')"
    read_response
    jq -e --argjson id "$id" '.id == $id and (.result.content[0].text != null)' <<<"$CSDBG_RESPONSE" >/dev/null
    CSDBG_ENVELOPE="$(jq -r '.result.content[0].text' <<<"$CSDBG_RESPONSE")"
    jq -e 'has("state") and has("data") and has("nextActions")' <<<"$CSDBG_ENVELOPE" >/dev/null
    CSDBG_RESULT="$(jq -c '.data' <<<"$CSDBG_ENVELOPE")"
}

send_request 1 initialize '{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"csdbg-smoke","version":"0.1"}}'
read_response
jq -e '.result.serverInfo.name == "csdbg"' <<<"$CSDBG_RESPONSE" >/dev/null

CSDBG_BREAKPOINT_LINE="$(rg -n 'var message = ' "$CSDBG_SOURCE" | cut -d: -f1)"
call_tool 2 add_breakpoint "$(jq -cn --arg file "$CSDBG_SOURCE" --argjson line "$CSDBG_BREAKPOINT_LINE" '{file:$file, line:$line}')"
CSDBG_BREAKPOINT_ID="$(jq -r '.id' <<<"$CSDBG_RESULT")"

call_tool 3 start_debug "$(jq -cn --arg program "$CSDBG_PROGRAM" '{program:$program, stopAtEntry:true}')"
jq -e '.status.state == "stopped"' <<<"$CSDBG_RESULT" >/dev/null

call_tool 4 continue_execution '{}'
jq -e --argjson line "$CSDBG_BREAKPOINT_LINE" '
    .status.state == "stopped"
    and .status.currentLocation.line == $line
    and (.status.currentLocation.context.lines | any(.number == $line and .isCurrent))
' <<<"$CSDBG_RESULT" >/dev/null

call_tool 5 get_call_stack '{}'
CSDBG_FRAME_ID="$(jq -r '.stackFrames[0].id' <<<"$CSDBG_RESULT")"

call_tool 51 get_scopes "$(jq -cn --argjson frameId "$CSDBG_FRAME_ID" '{frameId:$frameId}')"
CSDBG_LOCALS_REFERENCE="$(jq -r '.scopes[] | select(.name == "Locals") | .variablesReference' <<<"$CSDBG_RESULT")"
call_tool 52 get_variables "$(jq -cn --argjson variablesReference "$CSDBG_LOCALS_REFERENCE" '{variablesReference:$variablesReference}')"
jq -e '.variables | any(.name == "person")' <<<"$CSDBG_RESULT" >/dev/null

call_tool 6 evaluate_expression "$(jq -cn --argjson frameId "$CSDBG_FRAME_ID" '{expression:"person.Name", frameId:$frameId}')"
jq -e '.result | contains("Ada")' <<<"$CSDBG_RESULT" >/dev/null

send_request 7 tools/call "$(jq -cn --argjson frameId "$CSDBG_FRAME_ID" '{name:"evaluate_expression", arguments:{expression:"person.ToString()", frameId:$frameId}}')"
read_response
jq -e '.id == 7 and .result.isError == true and ((.result.content[0].text | fromjson).error.message | contains("unsafe=true"))' <<<"$CSDBG_RESPONSE" >/dev/null

call_tool 8 remove_breakpoint "$(jq -cn --arg id "$CSDBG_BREAKPOINT_ID" '{id:$id}')"
jq -e '.status.breakpoints | length == 0' <<<"$CSDBG_RESULT" >/dev/null

call_tool 9 step_into '{}'
jq -e '.status.state == "stopped"' <<<"$CSDBG_RESULT" >/dev/null
call_tool 10 step_out '{}'
jq -e '.status.state == "stopped"' <<<"$CSDBG_RESULT" >/dev/null
call_tool 11 step_over '{}'
jq -e '.status.state == "stopped" or (.status.state == "terminated" and .status.exitCode == 0)' <<<"$CSDBG_RESULT" >/dev/null
call_tool 12 stop_debug '{}'

call_tool 121 set_exception_breakpoints '{"filters":["all"]}'
call_tool 122 start_debug "$(jq -cn --arg program "$CSDBG_PROGRAM" '{program:$program, args:["throw"]}')"
for ((attempt = 0; attempt < 40; attempt++)); do
    call_tool 123 get_status '{}'
    if [[ "$(jq -r '.state' <<<"$CSDBG_ENVELOPE")" == "stopped" ]]; then
        break
    fi
    sleep 0.05
done
[[ "$(jq -r '.state' <<<"$CSDBG_ENVELOPE")" == "stopped" ]]
call_tool 124 get_exception_info '{}'
jq -e '.exception.exceptionId | contains("InvalidOperationException")' <<<"$CSDBG_RESULT" >/dev/null
call_tool 125 stop_debug '{}'
call_tool 126 set_exception_breakpoints '{"filters":[]}'

call_tool 13 start_debug "$(jq -cn --arg program "$CSDBG_PROGRAM" '{program:$program, args:["loop"]}')"
CSDBG_THREAD_ID=""
for ((attempt = 0; attempt < 20; attempt++)); do
    call_tool 14 get_threads '{}'
    if jq -e '.threads | length > 0' <<<"$CSDBG_RESULT" >/dev/null; then
        CSDBG_THREAD_ID="$(jq -r '.threads[0].id' <<<"$CSDBG_RESULT")"
        break
    fi
    sleep 0.05
done
[[ -n "$CSDBG_THREAD_ID" ]]
call_tool 15 pause_execution "$(jq -cn --argjson threadId "$CSDBG_THREAD_ID" '{threadId:$threadId}')"
jq -e '.status.state == "stopped"' <<<"$CSDBG_RESULT" >/dev/null

send_request 16 tools/call '{"name":"continue_execution","arguments":{}}'
sleep 0.2
send_request 17 tools/call "$(jq -cn --argjson threadId "$CSDBG_THREAD_ID" '{name:"pause_execution", arguments:{threadId:$threadId}}')"
read_response
CSDBG_FIRST_ID="$(jq -r '.id' <<<"$CSDBG_RESPONSE")"
jq -e '(.id == 16 or .id == 17) and ((.result.content[0].text | fromjson).data.status.state == "stopped")' <<<"$CSDBG_RESPONSE" >/dev/null
read_response
CSDBG_SECOND_ID="$(jq -r '.id' <<<"$CSDBG_RESPONSE")"
jq -e '(.id == 16 or .id == 17) and ((.result.content[0].text | fromjson).data.status.state == "stopped")' <<<"$CSDBG_RESPONSE" >/dev/null
[[ "$CSDBG_FIRST_ID" != "$CSDBG_SECOND_ID" ]]

call_tool 18 stop_debug '{}'

dotnet "$CSDBG_PROGRAM" loop >/dev/null 2>&1 &
CSDBG_ATTACH_PROCESS="$!"
call_tool 19 attach_debug "$(jq -cn --argjson processId "$CSDBG_ATTACH_PROCESS" '{processId:$processId}')"
call_tool 20 get_threads '{}'
CSDBG_ATTACH_THREAD_ID="$(jq -r '.threads[0].id' <<<"$CSDBG_RESULT")"
if [[ "$(jq -r '.state' <<<"$CSDBG_ENVELOPE")" == "running" ]]; then
    call_tool 21 pause_execution "$(jq -cn --argjson threadId "$CSDBG_ATTACH_THREAD_ID" '{threadId:$threadId}')"
fi
call_tool 22 stop_debug '{}'
kill -0 "$CSDBG_ATTACH_PROCESS"
kill "$CSDBG_ATTACH_PROCESS"
wait "$CSDBG_ATTACH_PROCESS" 2>/dev/null || true
CSDBG_ATTACH_PROCESS=""

printf 'MCP smoke test passed\n'
