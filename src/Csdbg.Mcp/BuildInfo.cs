using System.Reflection;

internal static class BuildInfo
{
    public static string Version { get; } = GetVersion();

    private static string GetVersion()
    {
        var informationalVersion = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return informationalVersion?.Split('+', 2)[0] ?? "unknown";
    }
}
