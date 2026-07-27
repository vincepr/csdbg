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
        var parts = informationalVersion?.Split('+', 2);
        var packageVersion = parts is { Length: > 0 }
            && !string.IsNullOrWhiteSpace(parts[0])
                ? parts[0]
                : "unknown";
        var revision = parts is { Length: 2 } && IsSourceRevision(parts[1])
            ? parts[1].ToLowerInvariant()
            : null;
        return new(
            packageVersion,
            revision,
            revision is null ? "unavailable" : "assembly_metadata");
    }

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
