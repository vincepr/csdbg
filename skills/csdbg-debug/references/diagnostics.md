# Focused diagnostics

## Large collections

- Inspect count and shape first. For `List<T>`, expand `_items`, then page only
  needed array ranges; do not recursively traverse the collection.
- netcoredbg does not support lambda or cast expressions. Prefer existing
  precompiled query methods when available.
- Function evaluation times out after roughly five seconds. Do not synchronously
  serialize large graphs from `evaluate_expression`.
- Export only through an existing trusted target helper. Prefer atomic
  background output, continue execution, then inspect only file metadata and
  bounded external queries such as streaming `jq`.

## HTTP failures

- Select the caller frame retaining `HttpResponseMessage`.
- With user authorization, evaluate
  `response.Content.ReadAsStringAsync().GetAwaiter().GetResult()` using
  `unsafe=true`.
- Treat bodies and headers as sensitive. Reading non-buffered content may
  consume it or execute user code.

## EF Core and PostgreSQL

- Exact executed SQL requires logging, diagnostics, or a command interceptor
  enabled before reproduction. Inspect command text and parameters separately.
- Expand the runtime exception object instead of casting it. Check `SqlState`,
  `MessageText`, `Detail`, `TableName`, `ColumnName`, and `ConstraintName`.
- `Include Error Detail` and sensitive-data logging may expose secrets or user
  data. Enable them only for an explicit debugging session and restore the
  original configuration afterward.
