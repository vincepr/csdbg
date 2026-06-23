namespace Csdbg.Core;

public static class BackendLocator
{
    public static BackendInfo FindNetcoredbg()
    {
        var path = FindExecutable(Environment.GetEnvironmentVariable("CSDBG_NETCOREDBG"))
            ?? FindExecutable(Environment.GetEnvironmentVariable("NETCOREDBG_PATH"))
            ?? FindExecutable("netcoredbg");

        return path is null
            ? new BackendInfo
            {
                Path = null,
                Error = "netcoredbg not found. Set CSDBG_NETCOREDBG or NETCOREDBG_PATH, or put netcoredbg on PATH."
            }
            : new BackendInfo { Path = path };
    }

    private static string? FindExecutable(string? nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
        {
            return null;
        }

        if (Path.IsPathRooted(nameOrPath) || nameOrPath.Contains(Path.DirectorySeparatorChar))
        {
            return File.Exists(nameOrPath) ? Path.GetFullPath(nameOrPath) : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, nameOrPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (OperatingSystem.IsWindows())
            {
                var exeCandidate = $"{candidate}.exe";
                if (File.Exists(exeCandidate))
                {
                    return exeCandidate;
                }
            }
        }

        return null;
    }
}
