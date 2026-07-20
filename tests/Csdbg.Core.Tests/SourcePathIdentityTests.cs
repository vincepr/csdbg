namespace Csdbg.Core.Tests;

public sealed class SourcePathIdentityTests
{
    [Fact]
    public void GetComparer_ForWindows_IsCaseInsensitive()
    {
        var paths = new HashSet<string>(SourcePathIdentity.GetComparer(isWindows: true))
        {
            @"C:\src\Project\Program.cs"
        };

        Assert.Contains(@"c:\SRC\project\program.CS", paths);
    }

    [Fact]
    public void GetComparer_ForUnix_IsCaseSensitive()
    {
        var paths = new HashSet<string>(SourcePathIdentity.GetComparer(isWindows: false))
        {
            "/src/Project/Program.cs"
        };

        Assert.DoesNotContain("/src/project/Program.cs", paths);
    }
}
