using System.Reflection;

internal static class BuildInfo
{
    public static BuildProvenance Provenance { get; } = FromInformationalVersion(
        typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion);

    public static string Version => Provenance.PackageVersion;

    internal static BuildProvenance FromInformationalVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return new("unknown", null, "unavailable");
        }

        var metadataSeparator = informationalVersion.IndexOf('+');
        if (metadataSeparator < 0)
        {
            return new(informationalVersion, null, "unavailable");
        }

        var version = informationalVersion[..metadataSeparator];
        var metadata = informationalVersion[(metadataSeparator + 1)..];
        string packageVersion;
        string? revision;

        // The SDK appends SourceRevisionId as either the complete build metadata
        // or its final dot-delimited identifier when package metadata already exists.
        if (IsSourceRevision(metadata))
        {
            packageVersion = version;
            revision = metadata;
        }
        else if (metadata.LastIndexOf('.') is var revisionSeparator
            && revisionSeparator > 0
            && IsBuildMetadata(metadata[..revisionSeparator])
            && IsSourceRevision(metadata[(revisionSeparator + 1)..]))
        {
            packageVersion = $"{version}+{metadata[..revisionSeparator]}";
            revision = metadata[(revisionSeparator + 1)..];
        }
        else
        {
            packageVersion = informationalVersion;
            revision = null;
        }

        return new(
            packageVersion,
            revision?.ToLowerInvariant(),
            revision is null ? "unavailable" : "assembly_metadata");
    }

    private static bool IsBuildMetadata(string value) =>
        value.Split('.').All(identifier =>
            identifier.Length > 0 && identifier.All(character =>
                character is >= '0' and <= '9'
                    or >= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or '-'));

    private static bool IsSourceRevision(string value) =>
        value.Length == 40 && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');
}

internal sealed record BuildProvenance(
    string PackageVersion,
    string? SourceRevision,
    string SourceRevisionCapability);
