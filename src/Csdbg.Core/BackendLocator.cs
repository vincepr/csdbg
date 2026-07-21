namespace Csdbg.Core;

public static class BackendLocator
{
    public static BackendInfo FindNetcoredbg()
    {
        return FindNetcoredbg(
            Environment.GetEnvironmentVariable,
            OperatingSystem.IsWindows(),
            FindManagedExecutable());
    }

    public static BackendInfo FindNetcoredbg(
        Func<string, string?> getEnvironmentVariable,
        bool isWindows,
        string? managedExecutablePath = null)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var searchPath = getEnvironmentVariable("PATH");
        var path = FindExecutable(getEnvironmentVariable("CSDBG_NETCOREDBG"), searchPath, isWindows)
            ?? FindExecutable(getEnvironmentVariable("NETCOREDBG_PATH"), searchPath, isWindows)
            ?? FindExecutable(managedExecutablePath, searchPath, isWindows)
            ?? FindExecutable("netcoredbg", searchPath, isWindows);

        return path is null
            ? new BackendInfo
            {
                Path = null,
                Error = "netcoredbg not found. Run csdbg --install-netcoredbg, set CSDBG_NETCOREDBG, or put netcoredbg on PATH."
            }
            : new BackendInfo { Path = path };
    }

    private static string? FindManagedExecutable()
    {
        try
        {
            var asset = NetcoredbgRelease.GetCurrentAsset();
            return BackendInstallPaths.GetExecutablePath(BackendInstallPaths.GetInstallRoot(), asset);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static string? FindExecutable(
        string? nameOrPath,
        string? searchPath,
        bool isWindows)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
        {
            return null;
        }

        if (Path.IsPathRooted(nameOrPath) || nameOrPath.Contains('/') || nameOrPath.Contains('\\'))
        {
            return IsExecutableFile(nameOrPath, isWindows) ? Path.GetFullPath(nameOrPath) : null;
        }

        var pathSeparator = isWindows ? ';' : ':';
        foreach (var dir in (searchPath ?? "").Split(pathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, nameOrPath);
            if (IsExecutableFile(candidate, isWindows))
            {
                return Path.GetFullPath(candidate);
            }

            if (isWindows && !nameOrPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var exeCandidate = $"{candidate}.exe";
                if (IsExecutableFile(exeCandidate, isWindows))
                {
                    return Path.GetFullPath(exeCandidate);
                }
            }
        }

        return null;
    }

    private static bool IsExecutableFile(string path, bool isWindows)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (isWindows || OperatingSystem.IsWindows())
        {
            return true;
        }

        var mode = File.GetUnixFileMode(path);
        const UnixFileMode executeBits =
            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return (mode & executeBits) != 0;
    }
}
