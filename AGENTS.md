# Repository Instructions

Keep the implementation small, IDE-independent, and structured around the `Csdbg.Core` and `Csdbg.Mcp` boundaries.

## Validation

- Run `dotnet test csdbg.slnx --configuration Release` before committing production changes.
- Add a unit or integration regression test for every defect found.

## Releases

- The NuGet package version is `<Version>` in `src/Csdbg.Mcp/Csdbg.Mcp.csproj`.
- Every version bump must include a matching `README.md` changelog entry in the same commit.
- Add changelog entries newest first using `### <version> - <YYYY-MM-DD>` and summarize user-visible changes.
- Update other version references in the repository when the package version changes.
- Never modify or republish an existing NuGet version; publish a new version instead.
