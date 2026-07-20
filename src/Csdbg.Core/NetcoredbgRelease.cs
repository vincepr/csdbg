using System.Runtime.InteropServices;

namespace Csdbg.Core;

public enum HostOperatingSystem
{
    Linux,
    MacOS,
    Windows
}

public enum BackendArchiveFormat
{
    TarGzip,
    Zip
}

public sealed record NetcoredbgReleaseAsset(
    string Tag,
    string Commit,
    string FileName,
    string Sha256,
    BackendArchiveFormat ArchiveFormat,
    string ExecutableName)
{
    public Uri DownloadUri => new(
        $"https://github.com/Samsung/netcoredbg/releases/download/{Tag}/{FileName}");
}

public static class NetcoredbgRelease
{
    public const string Tag = "3.2.0-1092";
    public const string Commit = "9744e1f051866215611b8440c638042aa2aa2f72";

    public static NetcoredbgReleaseAsset GetCurrentAsset() =>
        GetAsset(GetCurrentOperatingSystem(), RuntimeInformation.ProcessArchitecture);

    public static NetcoredbgReleaseAsset GetAsset(
        HostOperatingSystem operatingSystem,
        Architecture architecture)
    {
        return (operatingSystem, architecture) switch
        {
            (HostOperatingSystem.Linux, Architecture.X64) => Create(
                "netcoredbg-linux-amd64.tar.gz",
                "080eb3b2d2152465f599d3b33d1ee6e747794e11cc0a3773ec689f5e5f2c5afa",
                BackendArchiveFormat.TarGzip,
                "netcoredbg"),
            (HostOperatingSystem.Linux, Architecture.Arm64) => Create(
                "netcoredbg-linux-arm64.tar.gz",
                "065ff49badec8a695dbea2de6ab6a330c774a191e426a217ab8cc05250627ccb",
                BackendArchiveFormat.TarGzip,
                "netcoredbg"),
            (HostOperatingSystem.MacOS, Architecture.Arm64) => Create(
                "netcoredbg-osx-arm64.zip",
                "f4fa33b3ff874910cc184b4bb3b9c56d0abdf5c6521cee0b144d7c6e4a6e59ea",
                BackendArchiveFormat.Zip,
                "netcoredbg"),
            (HostOperatingSystem.Windows, Architecture.X64) => Create(
                "netcoredbg-win64.zip",
                "3c410a45fa502415203a94fcb88654af65bf8e3dac158a5527a722e7a6b9274a",
                BackendArchiveFormat.Zip,
                "netcoredbg.exe"),
            _ => throw new PlatformNotSupportedException(
                $"netcoredbg {Tag} has no managed-install asset for {operatingSystem}/{architecture}.")
        };
    }

    public static HostOperatingSystem GetCurrentOperatingSystem()
    {
        if (OperatingSystem.IsLinux())
        {
            return HostOperatingSystem.Linux;
        }

        if (OperatingSystem.IsMacOS())
        {
            return HostOperatingSystem.MacOS;
        }

        if (OperatingSystem.IsWindows())
        {
            return HostOperatingSystem.Windows;
        }

        throw new PlatformNotSupportedException("Only Linux, macOS, and Windows hosts are supported.");
    }

    private static NetcoredbgReleaseAsset Create(
        string fileName,
        string sha256,
        BackendArchiveFormat archiveFormat,
        string executableName) =>
        new(Tag, Commit, fileName, sha256, archiveFormat, executableName);
}

public static class BackendInstallPaths
{
    public static string GetInstallRoot() =>
        GetInstallRoot(Environment.GetEnvironmentVariable, NetcoredbgRelease.GetCurrentOperatingSystem());

    public static string GetInstallRoot(
        Func<string, string?> getEnvironmentVariable,
        HostOperatingSystem operatingSystem)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var xdgDataHome = getEnvironmentVariable("XDG_DATA_HOME");
        var baseDirectory = operatingSystem switch
        {
            HostOperatingSystem.Linux => !string.IsNullOrWhiteSpace(xdgDataHome)
                && Path.IsPathRooted(xdgDataHome)
                    ? xdgDataHome
                    : CombineHome(getEnvironmentVariable("HOME"), ".local", "share"),
            HostOperatingSystem.MacOS => CombineHome(
                getEnvironmentVariable("HOME"),
                "Library",
                "Application Support"),
            HostOperatingSystem.Windows => getEnvironmentVariable("LOCALAPPDATA"),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new InvalidOperationException(
                $"Cannot determine the per-user data directory for {operatingSystem}.");
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, "csdbg", "backends"));
    }

    public static string GetVersionDirectory(string installRoot, NetcoredbgReleaseAsset asset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentNullException.ThrowIfNull(asset);
        return Path.Combine(Path.GetFullPath(installRoot), "netcoredbg", asset.Tag);
    }

    public static string GetExecutablePath(string installRoot, NetcoredbgReleaseAsset asset) =>
        Path.Combine(GetVersionDirectory(installRoot, asset), "netcoredbg", asset.ExecutableName);

    private static string? CombineHome(string? home, params string[] parts) =>
        string.IsNullOrWhiteSpace(home)
            ? null
            : Path.Combine([home, .. parts]);

}
