# Repository Instructions

Keep the implementation small, IDE-independent, and structured around the `Csdbg.Core` and `Csdbg.Mcp` boundaries.

## Validation

- Run `dotnet test Csdbg.slnx --configuration Release` before committing production changes.
- Add a unit or integration regression test for every defect found.

## Releases

- The NuGet package version is `<Version>` in `src/Csdbg.Mcp/Csdbg.Mcp.csproj`.
- Every version bump must include a matching `README.md` changelog entry in the same commit.
- Add changelog entries newest first using `### <version> - <YYYY-MM-DD>` and summarize user-visible changes.
- Update other version references in the repository when the package version changes.
- Never modify or republish an existing NuGet version; publish a new version instead.
- Merge and push the reviewed release commit to `main`, then let the `Publish .NET tool` workflow publish it.
- After publishing, verify the exact new version on NuGet.org.
- Update or install that exact global `Csdbg.Mcp` version on the local system using the platform's `dotnet` tool command, then verify `csdbg --version`.
- Never expose publishing credentials or other secrets.
