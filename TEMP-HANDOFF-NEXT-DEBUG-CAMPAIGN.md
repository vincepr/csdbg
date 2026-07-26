# Canonical Handoff: csdbg Agentic Improvement Workflow

Date: 2026-07-26

This is the single entry point for the next csdbg workflow. Point the
orchestrator agent to this file. Files under `tmp-integration-feedback/` are
supporting evidence, not independent instructions. Their status text may be
historical; use the audit in this handoff as authoritative.

## Mission

Improve csdbg as a compact, headless, agent-first .NET debugger. Implement the
remaining product changes one at a time through fresh implementation agents,
independent review gates, atomic commits, fast-forward merges, and automatic
pushes. This implementation lane must immediately work through the confirmed
findings in the sequential backlog; it is not another discovery exercise.

In parallel, continue exploratory debugging in isolated target worktrees. That
parallel lane is read-only against the csdbg repository so it cannot conflict
with the implementation agent. It may discover additional work, but it does not
replace or delay implementation of the findings already listed here.

Runtime evidence and small maintainable changes take priority over feature
count. Do not broaden csdbg to JavaScript, TypeScript, or Rust. Use
`microsoft/DebugMCP` when VS Code-backed multi-language debugging is desired.

## Repository State

- Repository: `D:\coding\csdbg`
- Remote: `https://github.com/vincepr/csdbg`
- Implementation baseline immediately before this handoff:
  `fb198ffe0e1965965cde4078afbbf65c9308fbc5`
- Package version: `0.2.1`
- Backend used in retained campaigns: netcoredbg `3.2.0-1092`
- Release baseline: 240 core tests + 66 MCP tests = 306 passing
- `TEMP-HANDOFF-NEXT-DEBUG-CAMPAIGN.md` and
  `tmp-integration-feedback/` were untracked before this handoff update.
- Full solution `dotnet format --verify-no-changes` currently reports a
  pre-existing whitespace block in
  `tests/Csdbg.Core.Tests/BackendArchiveExtractorTests.cs:351-360`.
  Changed-file formatting passes.

The current response-efficiency work is merged and pushed:

- `736da5f`: prefer focused evaluation over mandatory DAP traversal
- `f25968c`: remove `nextActions` from successful responses
- `3f1c9b0`: return compact execution snapshots
- `7448901`: expose and invalidate the current top `frameId`
- `fb198ff`: add response-contract and skill regression coverage

Earlier campaign fixes are also on `main`:

- `db606e5`: accept MCP `_meta`, add `wait_for_stop`, add the skill
- `b7f8cf9`: validate timeouts and harden breakpoint/session recovery
- `f4defe6`: improve running-session thread discovery guidance
- `8cf33d8`: classify lambda arrows correctly
- `d23766f`: add optional large-collection, EF/Npgsql, and HTTP guidance
- `3853e40`: warn that debugger values and output are sensitive

Always fetch and confirm these assumptions before starting. Use the exact local
Release build from the selected commit; the globally installed `csdbg 0.2.1`
may not contain later source changes.

## Decisions That Must Not Be Reopened Without New Evidence

- Keep csdbg headless, IDE-independent, and .NET/netcoredbg-specific.
- Keep the 19 narrow typed MCP tools. Do not group them into `oneOf`, a gateway
  tool, or dynamically changing profiles.
- Do not build a CLI/MCP hybrid or daemon-backed CLI now.
- Do not add task-specific tools such as `count_where`, `get_ef_sql`, HTTP-body
  recovery, or breakpoint hit counting.
- Do not add a generic object-export MCP tool. Existing evidence does not show
  a safe arbitrary-object design.
- Do not return all locals, stacks, or source automatically at every stop.
- Do not restore mandatory
  `get_threads -> get_call_stack -> get_scopes -> get_variables` traversal.
- Do not change csdbg to address the Codex App transport hang unless raw stdio
  reproduces a csdbg-side failure.
- Do not modify `vincepr/TestingFixtures` from this repository.
- Do not implement JavaScript, TypeScript, or Rust adapters.

## Feedback Relevance Audit

| Artifact | Current disposition |
| --- | --- |
| `tmp-integration-feedback/README.md` | Historical index. `_meta` and `wait_for_stop` are merged, not merely fixed locally. |
| `campaign-summary.md` | Retained evidence. Test counts and "next experiments" predate the five response-efficiency commits. |
| `debug-skill-comparison.md` | Historical measurement baseline. Its broad-traversal and repeated-response observations are partly resolved. Re-run before using its token ratios as current results. |
| `issue-response-efficiency.md` | Partly resolved by `736da5f` through `fb198ff`. Focused workflow, compact execution snapshots, and successful-response `nextActions` are done. Replay acceptance, selective values, pseudo-property handling, and text-wrapped JSON remain open. |
| `issue-mcp-contract.md` | Partly resolved. `_meta` and routine `nextActions` are fixed. Structured output, stable typed errors, multi-client replay, and repository-owned conformance evidence remain open. |
| `issue-long-session-transport-hang.md` | Still relevant as an external/client investigation. Raw stdio completed 556 requests, so it is not an implementation item unless a direct csdbg reproduction appears. |
| `issue-sensitive-output.md` | Open, highest-priority product/security work. |
| `issue-evaluation-safety.md` | Open. Coordinate with sensitive-output work; variable expansion can invoke getters too. |
| `issue-variable-traversal.md` | Open. Default skill behavior is now more focused, but child-count metadata, pseudo-properties, and bounded discovery remain relevant. |
| `issue-large-object-analysis.md` | Evidence and design guardrail. Paging works; generic export remains explicitly deferred. Companion dump/ClrMD work is an experiment, not an automatic feature. |
| `issue-framework-diagnostics.md` | Default implementation work is complete in `references/diagnostics.md`. Streaming/binary HTTP and richer EF/PostgreSQL cases remain campaign scenarios. |
| `issue-exit-and-exception-semantics.md` | Open correctness issue. An unhandled Windows exception was observed as exit code zero. |
| `issue-launch-observability.md` | Open. Invalid-path delay, target PID visibility, and bounded launch diagnostics remain relevant. |
| `testingfixtures-options-feedback.md` | Relevant only to `vincepr/TestingFixtures`; exclude it from csdbg implementation. |

Temporary files may remain in place. Do not rewrite every issue merely to
update its status; this handoff is the normalized view.

## Required Skills

The orchestrator and agents should load only what their current item needs:

- `test-driven-development` for every behavior change
- `code-review` for each review gate
- `using-git-worktrees` for every code-writing agent
- `systematic-debugging` for reproduced defects
- `csdbg-debug` for live runtime campaigns
- `code-find` for framework/dependency source
- `dotnet-swap-package-to-local-project` only for unpublished package testing

## Orchestrator Model

Keep one small orchestrator context and use fresh subagents:

1. The primary workflow is the implementation campaign. One implementation
   agent owns one confirmed backlog item in one Git worktree.
2. One fresh reviewer checks only that committed diff against the item
   specification and repository standards.
3. Findings go back to the same implementation agent. Do not create a second
   fixer with overlapping context.
4. After a clean review, the orchestrator independently runs the final gates,
   pushes the feature branch, fast-forwards `main`, and pushes `main`.
5. Close agents after their item. Start the next implementation agent from the
   new `origin/main`.

Implementation items are sequential and mandatory. Parallelism is reserved for
exploratory campaign agents whose workspaces, debug adapters, target processes,
databases, and evidence directories do not overlap. "Read-only" applies only to
their access to csdbg source; it does not describe the implementation campaign.

## Worktree and Branch Rules

Use Git worktrees, not Git subtrees.

For implementation item `<id>`:

```text
branch:   codex/<id>
worktree: D:\coding\ask\.worktrees\csdbg-<id>
base:     latest origin/main
```

Before creating it:

1. `git fetch origin --prune`
2. Confirm `main == origin/main`.
3. Confirm the original checkout has no tracked changes. Ignore, but do not
   stage or delete, the retained temporary feedback files.
4. Confirm the branch and worktree path do not already exist.

Never let two implementation worktrees edit csdbg concurrently. Never share
one worktree between agents. Never force-push `main`.

After a successful merge, the orchestrator may remove the clean merged
worktree and local feature branch after confirming the commit is contained in
`origin/main`. Preserve evidence directories.

## Per-Item Implementation Loop

Every item follows this exact loop:

1. Create a fresh worktree and branch from latest `origin/main`.
2. Read `AGENTS.md` and this handoff. The item specification below is complete.
   When present, named files under the original checkout's
   `D:\coding\csdbg\tmp-integration-feedback` directory are optional supporting
   evidence; do not require or copy them into the feature worktree.
3. Run the Release baseline:

   ```powershell
   dotnet test Csdbg.slnx --configuration Release --nologo --verbosity minimal
   ```

4. For a defect, reproduce and establish root cause before editing.
5. Add the smallest meaningful failing test and observe RED.
6. Implement only enough production behavior for GREEN.
7. Run focused tests, relevant project tests, then the full Release suite.
8. Run `git diff --check` and formatting verification. After backlog item 0,
   require full solution formatting; before it, verify every changed file.
9. Commit once with a descriptive message. Do not commit evidence, credentials,
   generated target output, or unrelated cleanup.
10. Start a fresh read-only reviewer with:
    - fixed base SHA
    - exact commit SHA
    - item specification and acceptance criteria
    - `AGENTS.md`
11. If findings exist, send them to the implementation agent, amend or add one
    correction commit, rerun all gates, and review again.
12. When clean:

    ```powershell
    git push -u origin codex/<id>
    # In the main checkout after fetching:
    git merge --ff-only codex/<id>
    git push origin main
    ```

    If `origin/main` moved, rebase the feature branch onto it and rerun all
    tests and review. Never resolve this with a force-push to `main`.

Do not merge an item with unresolved review findings, failing tests, unexplained
warnings, unbounded output, or missing regression coverage.

## Sequential Implementation Backlog

This backlog implements the findings already produced by the previous debug
campaign. Items 0 and 1 establish clean, measurable gates; they must not turn
into an open-ended research phase. Items 2 through 8 are the confirmed product
changes. Item 9 is explicitly optional and remains experimental unless its
merge criteria are met.

### 0. Restore a Clean Formatting Baseline

Scope:

- Format only the pre-existing block in
  `BackendArchiveExtractorTests.cs:351-360`.
- Confirm no behavior changes.

Acceptance:

- Full `dotnet format Csdbg.slnx --verify-no-changes --no-restore` passes.
- All 306+ Release tests pass.
- One formatting-only commit.

### 1. Establish a Repository-Owned Replay Baseline

References:

- `issue-response-efficiency.md`
- `issue-mcp-contract.md`
- `debug-skill-comparison.md`
- Existing external scheduler evidence under
  `D:\coding\ask\.comparisons\evidence`

Scope:

- Add a deterministic, sanitized replay/conformance fixture owned by this
  repository.
- Measure tool calls, request/response characters, response shapes, stale DAP
  references, termination, and cleanup.
- Re-run the focused scheduler diagnosis against current `main`.

Acceptance:

- Equivalent diagnosis completes in at most 20 csdbg calls.
- Model-visible debugger responses total at most 30 KB.
- Replay contains no secrets and is deterministic in CI.
- Existing 19 tool schemas remain unchanged.

If the limits fail, first adjust focused workflow or repeated response shape.
Do not add a broad stopped-state snapshot endpoint automatically.

### 2. Replace Message-Based Error Classification

References:

- `issue-mcp-contract.md`
- `issue-evaluation-safety.md`

Scope:

- Introduce stable internal exception/error categories instead of classifying
  primarily from message text.
- Cover invalid arguments, wrong state, unknown breakpoint, unsupported
  expression syntax, target evaluation exception, evaluation timeout, stale
  frame/reference, adapter failure, and transport failure where the backend
  permits distinction.
- Preserve concise, actionable MCP errors.

Acceptance:

- Error code tests do not depend on localized backend text.
- Unknown breakpoint removal has a deliberate stable result.
- Session remains reusable after recoverable adapter/evaluation errors.
- No new MCP endpoint.

### 3. Add Names/Types-Only Variable Discovery

References:

- `issue-sensitive-output.md`
- `issue-variable-traversal.md`

Scope:

- Add the smallest typed optional mode to `get_variables` that can return names,
  types, presentation hints, child references/counts, and redaction metadata
  without returning values to MCP content.
- Keep selective paged value retrieval available.
- Apply limits before serialization.

Design gate:

- A design agent compares a boolean flag with a small value-mode enum.
- Prefer the schema that remains explicit and cannot accidentally reveal values.
- Review the design before implementation, then use a fresh implementation
  agent.

Acceptance:

- Routine discovery can inspect structure without exposing the PostgreSQL
  password fixture.
- Pagination and stale-reference behavior remain correct.
- Response tests assert exact fields and byte bounds.
- Existing value retrieval remains available only through an explicit mode.

### 4. Add a Sensitive-Output Boundary

References:

- `issue-sensitive-output.md`
- `issue-framework-diagnostics.md`

Scope:

- Introduce one shared redaction/result policy for variable values, evaluation
  results, exception details, recent output, and future artifact metadata.
- Recognize structured connection strings, authorization headers, common token
  containers, URIs, and JSON without relying only on variable names.
- Expose visible machine-readable redaction metadata.
- Make raw reveal explicit, scoped, and auditable.

Implement in narrow vertical slices:

1. policy model and variable values
2. evaluation and exception output
3. recent output

Each slice gets its own agent, commit, review, merge, and push.

Acceptance:

- Default inspection does not return seeded credentials.
- Redacted values cannot be mistaken for authentic target values.
- Explicit reveal is narrowly authorized per request and covered by tests.
- False-positive and nested-structure cases are tested.

### 5. Refine Evaluation and Getter Safety

References:

- `issue-evaluation-safety.md`
- `issue-variable-traversal.md`

Scope:

- Distinguish plain field/local inspection, possible getter/code execution, and
  explicit mutation.
- Account for DAP property evaluation and debugger-generated pseudo-properties,
  not only `evaluate_expression`.
- Keep ordinary field inspection ergonomic.

Acceptance:

- Tests cover getter mutation, blocking/throwing getters, method calls, and
  assignment/mutation.
- Risk classification and authorization are machine-readable.
- No rule merely marks every dotted expression unsafe while leaving variable
  expansion unguarded.

### 6. Improve Bounded Variable Traversal

References:

- `issue-variable-traversal.md`
- `issue-large-object-analysis.md`

Scope:

- Preserve and expose named/indexed child counts when adapters provide them.
- Separate ordinary children from debugger-generated pseudo-properties and
  failed property evaluations.
- Return actionable adapter errors without poisoning the session.
- Keep pagination explicit and bounded.

Acceptance:

- Large list first/last-page scenarios remain below the replay byte limit.
- Reflection/runtime graphs are not traversed by default.
- A failed pseudo-property can be skipped while sibling inspection continues.
- No generic export operation is added.

### 7. Correct Exit and Exception Semantics

Reference:

- `issue-exit-and-exception-semantics.md`

Scope:

- Capture raw DAP event order for `stopped`, `exited`, and `terminated`.
- Determine exit-code provenance and prevent an unhandled exception from being
  reported as successful exit zero.
- Surface exception phase only when the adapter provides defensible evidence;
  otherwise return an explicit unknown phase.

Acceptance:

- Regression covers the Windows `0xE0434352` case.
- Linux and macOS behavior is covered or explicitly capability-gated.
- Repeated exception stops are not mislabeled.
- Exit state remains compact and session cleanup remains deterministic.

### 8. Improve Launch Observability

Reference:

- `issue-launch-observability.md`

Implement as separate reviewed slices:

1. prompt invalid-program validation and reusable-session regression
2. bounded/categorized launch diagnostics
3. target PID exposure only if netcoredbg provides reliable lifecycle evidence

Acceptance:

- A clearly missing DLL fails promptly.
- Launch failure output is bounded and high signal.
- PID is not guessed from unrelated system processes.
- Attach remains non-destructive.
- Cross-platform tests cover any exposed process contract.

### 9. Evaluate Structured MCP Output

Reference:

- `issue-mcp-contract.md`

This begins as a non-merge experiment branch.

Scope:

- Compare current JSON-in-text results with MCP `structuredContent` and
  `outputSchema`.
- Test current Codex App/CLI plus at least one additional MCP client.
- Measure schema size, duplicated text, compatibility, startup/dependency cost,
  and test complexity.

Merge only if:

- clients receive structured data without requiring a duplicated full text
  payload;
- compatibility is not weakened;
- dependency and implementation growth remain small;
- replay shows a material response/context benefit.

Otherwise record the result and delete the experimental branch after retaining
the report.

## Explicitly Deferred Work

- Generic object export through MCP
- Millions-of-items traversal through DAP
- Companion ClrMD/dump CLI until a separate design proves demand and safety
- Grouped MCP, gateway tool, stateful CLI, or hybrid transport
- Domain-specific EF, PostgreSQL, or HTTP MCP tools
- JavaScript, TypeScript, Rust, or VS Code integration
- Fixes for the Codex App transport hang without raw-stdio reproduction

## Parallel Exploratory Debugging Campaign

Exploratory campaign work may run beside the sequential implementation lane
only when it is read-only against csdbg. The sequential lane continues
implementing confirmed findings while these scenarios run.

### Isolation

For each scenario `<scenario-id>`:

```text
target worktree:
  D:\coding\ask\.debug-scenarios\worktrees\<scenario-id>

evidence:
  D:\coding\ask\.debug-scenarios\evidence\<scenario-id>

csdbg build:
  a frozen Release build identified by exact csdbg commit SHA

debug transport:
  one dedicated csdbg stdio process
```

Rules:

- Maximum three ordinary scenario agents at once.
- Run timing, starvation, race, deadlock, and performance scenarios exclusively.
- Give every database scenario a unique Docker Compose project name, database,
  volume, and host port. Check Docker readiness first.
- Do not share one target checkout, testhost, csdbg process, PostgreSQL instance,
  output file, or evidence directory.
- Campaign agents must not edit, commit, merge, or push csdbg.
- A campaign finding becomes a sanitized issue file or orchestrator message.
  Only the sequential implementation lane may turn it into code.
- If a scenario needs instrumentation, commit it only in that disposable target
  worktree. Record the target commit in evidence.
- Never run benchmarks or timing-sensitive debugger comparisons in parallel.

### Campaign Agent Contract

Each agent receives:

- one scenario or tightly related scenario group
- exact csdbg SHA and target SHA
- expected evidence
- its own worktree and evidence path
- a prohibition on csdbg edits
- a byte/time/tool-call budget

Each result must include:

- baseline behavior without debugger
- runtime versions and OS
- exact tool sequence and stop reasons
- bounded request/response sizes
- root cause or explicit inconclusive result
- cleanup verification and second-session result
- sanitized transcript plus hashes for large artifacts
- recommendation classified as documentation, campaign evidence, external
  defect, or csdbg implementation candidate

## Debug Session Protocol

Use this for every campaign:

1. Record csdbg, netcoredbg, target, OS, architecture, SDK/runtime, and build
   revisions.
2. Build with symbols and reproduce once without a debugger.
3. Start a fresh csdbg server and call `get_status`.
4. Add the earliest useful breakpoint before launch/attach when possible.
5. For tests, identify the managed testhost rather than the parent `dotnet`
   process.
6. Start/attach and use `wait_for_stop` while running.
7. At a stop, use `currentLocation.frameId` and focused
   `evaluate_expression` first. Fetch call stack, scopes, or variables only
   when focused evaluation is insufficient or another frame is needed.
8. Re-fetch frame/scope/variable references after every resume.
9. Treat getters, methods, output, exceptions, SQL, HTTP bodies, and connection
   strings as sensitive and potentially side-effecting.
10. Continue to termination/detach, call `stop_debug`, verify process cleanup,
    and run a clean second session.
11. Store bounded sanitized JSONL. Large content stays in artifacts; record only
    path, size, hash, schema, and bounded samples.

## Campaign Matrix

Run independent groups in parallel where isolation permits. Preserve the
detailed evidence expectations from the issue files.

### A. Third-Party and Framework Code

1. Source Link NuGet dependency
2. Package without source/PDB
3. Mismatched assembly/PDB
4. Optimized generic/async dependency
5. Framework exception with first bad application input
6. Generated proxy/source-generated client
7. Reflection and `TargetInvocationException`
8. Assembly version/load-context conflict
9. Managed/native P/Invoke boundary

### B. Async, Concurrency, and Process Topology

10. Async exception ownership
11. Swallowed/unobserved task exception
12. Thread-pool starvation
13. Monitor deadlock
14. Race-dependent invariant
15. Cancellation chain
16. Parallel testhosts
17. Child managed worker
18. Non-destructive service attach

### C. Web, HTTP, and Serialization

19. Streaming HTTP error body
20. Large/binary HTTP body
21. Compressed response
22. Disposed response
23. ASP.NET request pipeline
24. JSON conversion failure
25. gRPC status/trailers/deadline

### D. EF Core and PostgreSQL

26. Parameterized SQL and bounded values
27. N+1 query
28. Transaction isolation/blocking
29. PostgreSQL deadlock
30. Command/client/server timeout distinction
31. Optimistic concurrency values
32. Migration drift
33. DbContext pooling contamination
34. Retry execution strategy and idempotency

### E. Data, Memory, and Evaluation Safety

35. Large dictionary/cyclic graph
36. Lazy enumerable with side effects
37. Property getter side effects
38. `DebuggerDisplay`, proxy, and `ToString()` side effects
39. Memory leak and DAP/dump boundary
40. Sensitive locals and explicit reveal
41. Evaluation failure taxonomy

### F. Runtime and Deployment

42. Single-file/trimmed application
43. ReadyToRun
44. AssemblyLoadContext/plugin reload
45. Container source-path mapping
46. Windows/Linux/macOS replay
47. Abrupt netcoredbg death
48. MCP client disconnect during lifecycle operations

## Evidence and Safety Rules

- Never commit credentials, raw connection strings, tokens, authorization
  headers, personal data, database dumps, or complete HTTP bodies.
- Sanitize before an artifact enters agent context or Git.
- Record every replacement in a redaction report.
- Put large artifacts outside the repository and record SHA-256, byte length,
  producing command, and deletion policy.
- Do not interpret redacted text as an authentic runtime value.
- Do not retain partial files from failed target-side export.
- Stop all debugger/test/database processes after each scenario.

## Relevant Artifacts

- Canonical workflow:
  `D:\coding\csdbg\TEMP-HANDOFF-NEXT-DEBUG-CAMPAIGN.md`
- Feedback evidence:
  `D:\coding\csdbg\tmp-integration-feedback`
- Existing campaign evidence:
  `D:\coding\ask\.debug-scenarios\evidence`
- Comparison evidence:
  `D:\coding\ask\.comparisons\evidence`
- Scheduler fixture:
  `D:\coding\ask\.comparisons\csdbg-scheduler`
- Raw post-cleanup reproduction:
  `D:\coding\ask\.comparisons\evidence\raw-csdbg-post-cleanup-repro.ps1`
- Current skill:
  `D:\coding\csdbg\skills\csdbg-debug\SKILL.md`
- Optional diagnostics:
  `D:\coding\csdbg\skills\csdbg-debug\references\diagnostics.md`

## First Orchestrator Actions

1. Read this file and `AGENTS.md`.
2. Fetch `origin` and verify the repository state and Release baseline.
3. Create the item-0 formatting worktree and run it through the full
   implementation/review/merge/push loop.
4. Create item 1 from the resulting `origin/main`.
5. In parallel, dispatch at most three read-only campaign groups using frozen
   csdbg builds and isolated target worktrees.
6. Reconcile campaign findings into the sequential queue only after evidence
   and review; do not let campaign agents merge code.
