using System.Runtime.InteropServices;

namespace Csdbg.Core.Tests;

public sealed class NetcoredbgReleaseTests
{
    public static TheoryData<
        HostOperatingSystem,
        Architecture,
        string,
        string,
        BackendArchiveFormat,
        string> SupportedAssets => new()
    {
        {
            HostOperatingSystem.Linux,
            Architecture.X64,
            "netcoredbg-linux-amd64.tar.gz",
            "080eb3b2d2152465f599d3b33d1ee6e747794e11cc0a3773ec689f5e5f2c5afa",
            BackendArchiveFormat.TarGzip,
            "netcoredbg"
        },
        {
            HostOperatingSystem.Linux,
            Architecture.Arm64,
            "netcoredbg-linux-arm64.tar.gz",
            "065ff49badec8a695dbea2de6ab6a330c774a191e426a217ab8cc05250627ccb",
            BackendArchiveFormat.TarGzip,
            "netcoredbg"
        },
        {
            HostOperatingSystem.MacOS,
            Architecture.Arm64,
            "netcoredbg-osx-arm64.zip",
            "f4fa33b3ff874910cc184b4bb3b9c56d0abdf5c6521cee0b144d7c6e4a6e59ea",
            BackendArchiveFormat.Zip,
            "netcoredbg"
        },
        {
            HostOperatingSystem.Windows,
            Architecture.X64,
            "netcoredbg-win64.zip",
            "3c410a45fa502415203a94fcb88654af65bf8e3dac158a5527a722e7a6b9274a",
            BackendArchiveFormat.Zip,
            "netcoredbg.exe"
        }
    };

    public static IEnumerable<object[]> UnsupportedAssets()
    {
        foreach (var operatingSystem in Enum.GetValues<HostOperatingSystem>())
        {
            foreach (var architecture in Enum.GetValues<Architecture>())
            {
                if (!IsSupported(operatingSystem, architecture))
                {
                    yield return [operatingSystem, architecture];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(SupportedAssets))]
    public void GetAsset_ReturnsExactPinnedReleaseMetadata(
        HostOperatingSystem operatingSystem,
        Architecture architecture,
        string fileName,
        string sha256,
        BackendArchiveFormat archiveFormat,
        string executableName)
    {
        var asset = NetcoredbgRelease.GetAsset(operatingSystem, architecture);

        Assert.Equal("3.2.0-1092", NetcoredbgRelease.Tag);
        Assert.Equal("9744e1f051866215611b8440c638042aa2aa2f72", NetcoredbgRelease.Commit);
        Assert.Equal(NetcoredbgRelease.Tag, asset.Tag);
        Assert.Equal(NetcoredbgRelease.Commit, asset.Commit);
        Assert.Equal(fileName, asset.FileName);
        Assert.Equal(sha256, asset.Sha256);
        Assert.Equal(archiveFormat, asset.ArchiveFormat);
        Assert.Equal(executableName, asset.ExecutableName);
        Assert.Equal(
            $"https://github.com/Samsung/netcoredbg/releases/download/3.2.0-1092/{fileName}",
            asset.DownloadUri.AbsoluteUri);
    }

    [Theory]
    [MemberData(nameof(UnsupportedAssets))]
    public void GetAsset_RejectsEveryUnsupportedOperatingSystemArchitecturePair(
        HostOperatingSystem operatingSystem,
        Architecture architecture)
    {
        var exception = Assert.Throws<PlatformNotSupportedException>(
            () => NetcoredbgRelease.GetAsset(operatingSystem, architecture));

        Assert.Equal(
            $"netcoredbg 3.2.0-1092 has no managed-install asset for {operatingSystem}/{architecture}.",
            exception.Message);
    }

    [Theory]
    [InlineData(HostOperatingSystem.MacOS, Architecture.X64)]
    [InlineData(HostOperatingSystem.Windows, Architecture.Arm64)]
    [InlineData(HostOperatingSystem.Linux, Architecture.X86)]
    [InlineData(HostOperatingSystem.Linux, Architecture.Arm)]
    public void GetAsset_RejectsKnownUnavailableAssets(
        HostOperatingSystem operatingSystem,
        Architecture architecture)
    {
        Assert.Throws<PlatformNotSupportedException>(
            () => NetcoredbgRelease.GetAsset(operatingSystem, architecture));
    }

    [Fact]
    public void GetAsset_RejectsUndefinedArchitecture()
    {
        Assert.Throws<PlatformNotSupportedException>(
            () => NetcoredbgRelease.GetAsset(
                HostOperatingSystem.Linux,
                (Architecture)int.MaxValue));
    }

    [Fact]
    public void GetCurrentOperatingSystem_MatchesTheRuntime()
    {
        var expected = OperatingSystem.IsLinux()
            ? HostOperatingSystem.Linux
            : OperatingSystem.IsMacOS()
                ? HostOperatingSystem.MacOS
                : HostOperatingSystem.Windows;

        Assert.Equal(expected, NetcoredbgRelease.GetCurrentOperatingSystem());
    }

    [Fact]
    public void GetCurrentAsset_MatchesTheExplicitCurrentPlatformAsset()
    {
        var operatingSystem = NetcoredbgRelease.GetCurrentOperatingSystem();
        var architecture = RuntimeInformation.ProcessArchitecture;

        if (IsSupported(operatingSystem, architecture))
        {
            Assert.Equal(
                NetcoredbgRelease.GetAsset(operatingSystem, architecture),
                NetcoredbgRelease.GetCurrentAsset());
        }
        else
        {
            Assert.Throws<PlatformNotSupportedException>(NetcoredbgRelease.GetCurrentAsset);
        }
    }

    [Fact]
    public void GetInstallRoot_OnLinux_PrefersXdgDataHomeOverHome()
    {
        var xdgDataHome = DenormalizedRoot("xdg");
        var home = DenormalizedRoot("home");
        var environment = CreateEnvironment(
            ("XDG_DATA_HOME", xdgDataHome),
            ("HOME", home));

        var result = BackendInstallPaths.GetInstallRoot(environment, HostOperatingSystem.Linux);

        AssertNormalizedPath(
            Path.Combine(xdgDataHome, "csdbg", "backends"),
            result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetInstallRoot_OnLinux_FallsBackToHomeWhenXdgDataHomeIsMissing(
        string? xdgDataHome)
    {
        var home = DenormalizedRoot("home");
        var environment = CreateEnvironment(
            ("XDG_DATA_HOME", xdgDataHome),
            ("HOME", home));

        var result = BackendInstallPaths.GetInstallRoot(environment, HostOperatingSystem.Linux);

        AssertNormalizedPath(
            Path.Combine(home, ".local", "share", "csdbg", "backends"),
            result);
    }

    [Fact]
    public void GetInstallRoot_OnLinux_IgnoresRelativeXdgDataHomeAndFallsBackToHome()
    {
        const string relativeXdgDataHome = "relative-xdg-data-home";
        var home = DenormalizedRoot("home");
        var environment = CreateEnvironment(
            ("XDG_DATA_HOME", relativeXdgDataHome),
            ("HOME", home));

        var result = BackendInstallPaths.GetInstallRoot(environment, HostOperatingSystem.Linux);

        AssertNormalizedPath(
            Path.Combine(home, ".local", "share", "csdbg", "backends"),
            result);
        Assert.NotEqual(
            Path.GetFullPath(Path.Combine(relativeXdgDataHome, "csdbg", "backends")),
            result);
    }

    [Fact]
    public void GetInstallRoot_OnMacOS_UsesHomeApplicationSupport()
    {
        var home = DenormalizedRoot("home");
        var environment = CreateEnvironment(("HOME", home));

        var result = BackendInstallPaths.GetInstallRoot(environment, HostOperatingSystem.MacOS);

        AssertNormalizedPath(
            Path.Combine(home, "Library", "Application Support", "csdbg", "backends"),
            result);
    }

    [Fact]
    public void GetInstallRoot_OnWindows_UsesLocalAppData()
    {
        var localAppData = DenormalizedRoot("local-app-data");
        var environment = CreateEnvironment(("LOCALAPPDATA", localAppData));

        var result = BackendInstallPaths.GetInstallRoot(environment, HostOperatingSystem.Windows);

        AssertNormalizedPath(
            Path.Combine(localAppData, "csdbg", "backends"),
            result);
    }

    [Theory]
    [InlineData(HostOperatingSystem.Linux)]
    [InlineData(HostOperatingSystem.MacOS)]
    [InlineData(HostOperatingSystem.Windows)]
    public void GetInstallRoot_ThrowsWhenRequiredEnvironmentIsMissing(
        HostOperatingSystem operatingSystem)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => BackendInstallPaths.GetInstallRoot(_ => " ", operatingSystem));

        Assert.Equal(
            $"Cannot determine the per-user data directory for {operatingSystem}.",
            exception.Message);
    }

    [Fact]
    public void GetInstallRoot_RejectsNullEnvironmentReader()
    {
        Assert.Throws<ArgumentNullException>(
            () => BackendInstallPaths.GetInstallRoot(null!, HostOperatingSystem.Linux));
    }

    [Theory]
    [MemberData(nameof(SupportedAssets))]
    public void VersionAndExecutablePaths_AreRootedNormalizedAndVersioned(
        HostOperatingSystem operatingSystem,
        Architecture architecture,
        string fileName,
        string sha256,
        BackendArchiveFormat archiveFormat,
        string executableName)
    {
        _ = fileName;
        _ = sha256;
        _ = archiveFormat;
        var installRoot = DenormalizedRoot("install");
        var asset = NetcoredbgRelease.GetAsset(operatingSystem, architecture);

        var versionDirectory = BackendInstallPaths.GetVersionDirectory(installRoot, asset);
        var executablePath = BackendInstallPaths.GetExecutablePath(installRoot, asset);

        AssertNormalizedPath(
            Path.Combine(installRoot, "netcoredbg", "3.2.0-1092"),
            versionDirectory);
        AssertNormalizedPath(
            Path.Combine(
                installRoot,
                "netcoredbg",
                "3.2.0-1092",
                "netcoredbg",
                executableName),
            executablePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetVersionDirectory_RejectsMissingInstallRoot(string? installRoot)
    {
        var asset = NetcoredbgRelease.GetAsset(HostOperatingSystem.Linux, Architecture.X64);

        Assert.ThrowsAny<ArgumentException>(
            () => BackendInstallPaths.GetVersionDirectory(installRoot!, asset));
    }

    [Fact]
    public void VersionAndExecutablePaths_RejectNullAsset()
    {
        var installRoot = DenormalizedRoot("install");

        Assert.Throws<ArgumentNullException>(
            () => BackendInstallPaths.GetVersionDirectory(installRoot, null!));
        Assert.Throws<ArgumentNullException>(
            () => BackendInstallPaths.GetExecutablePath(installRoot, null!));
    }

    private static bool IsSupported(
        HostOperatingSystem operatingSystem,
        Architecture architecture) =>
        (operatingSystem, architecture) is
            (HostOperatingSystem.Linux, Architecture.X64) or
            (HostOperatingSystem.Linux, Architecture.Arm64) or
            (HostOperatingSystem.MacOS, Architecture.Arm64) or
            (HostOperatingSystem.Windows, Architecture.X64);

    private static Func<string, string?> CreateEnvironment(
        params (string Name, string? Value)[] variables)
    {
        var environment = variables.ToDictionary(
            variable => variable.Name,
            variable => variable.Value,
            StringComparer.Ordinal);

        return name => environment.GetValueOrDefault(name);
    }

    private static string DenormalizedRoot(string name) =>
        Path.Combine(Path.GetTempPath(), "csdbg-release-tests", "discarded", "..", name);

    private static void AssertNormalizedPath(string expected, string actual)
    {
        Assert.True(Path.IsPathFullyQualified(actual));
        Assert.Equal(Path.GetFullPath(expected), actual);
        Assert.Equal(actual, Path.GetFullPath(actual));
    }
}
