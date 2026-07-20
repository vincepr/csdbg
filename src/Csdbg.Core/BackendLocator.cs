namespace Csdbg.Core;

public static class BackendLocator
{
    public static BackendInfo FindNetcoredbg()
    {
        return FindNetcoredbg(Environment.GetEnvironmentVariable, OperatingSystem.IsWindows());
    }

    public static BackendInfo FindNetcoredbg(
        Func<string, string?> getEnvironmentVariable,
        bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var searchPath = getEnvironmentVariable("PATH");
        var path = FindExecutable(getEnvironmentVariable("CSDBG_NETCOREDBG"), searchPath, isWindows)
            ?? FindExecutable(getEnvironmentVariable("NETCOREDBG_PATH"), searchPath, isWindows)
            ?? FindExecutable("netcoredbg", searchPath, isWindows);

        return path is null
            ? new BackendInfo
            {
                Path = null,
                Error = "netcoredbg not found. Set CSDBG_NETCOREDBG or NETCOREDBG_PATH, or put netcoredbg on PATH."
            }
            : new BackendInfo { Path = path };
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
