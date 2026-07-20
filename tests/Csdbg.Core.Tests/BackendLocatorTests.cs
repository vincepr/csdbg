namespace Csdbg.Core.Tests;

public sealed class BackendLocatorTests
{
    [Fact]
    public void FindNetcoredbg_PrefersPrimaryOverrideOverLegacyOverrideAndPath()
    {
        using var temp = new TempDirectory();
        var primary = temp.CreateExecutable("primary/netcoredbg");
        var legacy = temp.CreateExecutable("legacy/netcoredbg");
        var pathExecutable = temp.CreateExecutable("path/netcoredbg");
        var environment = CreateEnvironment(
            ("CSDBG_NETCOREDBG", primary),
            ("NETCOREDBG_PATH", legacy),
            ("PATH", Path.GetDirectoryName(pathExecutable)));

        var result = BackendLocator.FindNetcoredbg(environment, OperatingSystem.IsWindows());

        Assert.True(result.Available);
        Assert.Equal(Path.GetFullPath(primary), result.Path);
    }

    [Fact]
    public void FindNetcoredbg_FallsBackToLegacyOverride()
    {
        using var temp = new TempDirectory();
        var legacy = temp.CreateExecutable("legacy/netcoredbg");
        var pathExecutable = temp.CreateExecutable("path/netcoredbg");
        var environment = CreateEnvironment(
            ("CSDBG_NETCOREDBG", temp.GetPath("missing/netcoredbg")),
            ("NETCOREDBG_PATH", legacy),
            ("PATH", Path.GetDirectoryName(pathExecutable)));

        var result = BackendLocator.FindNetcoredbg(environment, OperatingSystem.IsWindows());

        Assert.True(result.Available);
        Assert.Equal(Path.GetFullPath(legacy), result.Path);
    }

    [Fact]
    public void FindNetcoredbg_SearchesPathWhenOverridesAreUnavailable()
    {
        using var temp = new TempDirectory();
        var expected = temp.CreateExecutable("second/netcoredbg");
        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var searchPath = string.Join(
            separator,
            temp.GetPath("first"),
            Path.GetDirectoryName(expected));
        Directory.CreateDirectory(temp.GetPath("first"));
        var environment = CreateEnvironment(
            ("CSDBG_NETCOREDBG", null),
            ("NETCOREDBG_PATH", null),
            ("PATH", searchPath));

        var result = BackendLocator.FindNetcoredbg(environment, OperatingSystem.IsWindows());

        Assert.True(result.Available);
        Assert.Equal(Path.GetFullPath(expected), result.Path);
    }

    [Fact]
    public void FindNetcoredbg_FallsBackToManagedExecutable()
    {
        using var temp = new TempDirectory();
        var managed = temp.CreateExecutable("managed/netcoredbg");
        var environment = CreateEnvironment(
            ("CSDBG_NETCOREDBG", null),
            ("NETCOREDBG_PATH", null),
            ("PATH", temp.GetPath("empty")));
        Directory.CreateDirectory(temp.GetPath("empty"));

        var result = BackendLocator.FindNetcoredbg(
            environment,
            OperatingSystem.IsWindows(),
            managed);

        Assert.True(result.Available);
        Assert.Equal(Path.GetFullPath(managed), result.Path);
    }

    [Fact]
    public void FindNetcoredbg_PrefersManagedExecutableOverPathExecutable()
    {
        using var temp = new TempDirectory();
        var pathExecutable = temp.CreateExecutable("path/netcoredbg");
        var managed = temp.CreateExecutable("managed/netcoredbg");
        var environment = CreateEnvironment(
            ("CSDBG_NETCOREDBG", null),
            ("NETCOREDBG_PATH", null),
            ("PATH", Path.GetDirectoryName(pathExecutable)));

        var result = BackendLocator.FindNetcoredbg(
            environment,
            OperatingSystem.IsWindows(),
            managed);

        Assert.True(result.Available);
        Assert.Equal(Path.GetFullPath(managed), result.Path);
    }

    [Theory]
    [InlineData("CSDBG_NETCOREDBG")]
    [InlineData("NETCOREDBG_PATH")]
    public void FindNetcoredbg_PrefersExplicitOverrideOverManagedExecutable(
        string overrideVariable)
    {
        using var temp = new TempDirectory();
        var explicitOverride = temp.CreateExecutable("override/netcoredbg");
        var managed = temp.CreateExecutable("managed/netcoredbg");
        var environment = CreateEnvironment(
            (overrideVariable, explicitOverride),
            ("PATH", null));

        var result = BackendLocator.FindNetcoredbg(
            environment,
            OperatingSystem.IsWindows(),
            managed);

        Assert.True(result.Available);
        Assert.Equal(Path.GetFullPath(explicitOverride), result.Path);
    }

    [Fact]
    public void FindNetcoredbg_OnWindows_AppendsExeSuffixDuringPathSearch()
    {
        using var temp = new TempDirectory();
        var expected = temp.CreateExecutable("bin/netcoredbg.exe", executable: false);
        var environment = CreateEnvironment(("PATH", Path.GetDirectoryName(expected)));

        var result = BackendLocator.FindNetcoredbg(environment, isWindows: true);

        Assert.True(result.Available);
        Assert.Equal(Path.GetFullPath(expected), result.Path);
    }

    [Theory]
    [InlineData("forward/netcoredbg")]
    [InlineData(@"backward\netcoredbg")]
    public void FindExecutable_AcceptsRootedPathsContainingEitherSlashStyle(string relativePath)
    {
        using var temp = new TempDirectory();
        var expected = temp.CreateExecutable(relativePath);

        var result = BackendLocator.FindExecutable(
            expected,
            searchPath: null,
            isWindows: OperatingSystem.IsWindows());

        Assert.Equal(Path.GetFullPath(expected), result);
    }

    [Fact]
    public void FindExecutable_RejectsMissingPathsAndDirectories()
    {
        using var temp = new TempDirectory();
        var directory = temp.GetPath("netcoredbg-directory");
        Directory.CreateDirectory(directory);

        Assert.Null(BackendLocator.FindExecutable(
            temp.GetPath("missing-netcoredbg"),
            searchPath: null,
            isWindows: OperatingSystem.IsWindows()));
        Assert.Null(BackendLocator.FindExecutable(
            directory,
            searchPath: null,
            isWindows: OperatingSystem.IsWindows()));
    }

    [Fact]
    public void FindExecutable_OnUnix_RejectsFileWithoutExecuteBit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var path = temp.CreateExecutable("netcoredbg", executable: false);

        var result = BackendLocator.FindExecutable(path, searchPath: null, isWindows: false);

        Assert.Null(result);
    }

    private static Func<string, string?> CreateEnvironment(
        params (string Name, string? Value)[] variables)
    {
        var environment = variables.ToDictionary(
            variable => variable.Name,
            variable => variable.Value,
            StringComparer.Ordinal);

        return name => environment.GetValueOrDefault(name);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), $"csdbg-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        private string Root { get; }

        public string GetPath(string relativePath) => Path.Combine(Root, relativePath);

        public string CreateExecutable(string relativePath, bool executable = true)
        {
            var path = GetPath(relativePath);
            var directory = Path.GetDirectoryName(path);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, "test executable");
            if (!OperatingSystem.IsWindows())
            {
                var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                if (executable)
                {
                    mode |= UnixFileMode.UserExecute;
                }

                File.SetUnixFileMode(path, mode);
            }

            return path;
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
