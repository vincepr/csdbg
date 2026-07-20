namespace Csdbg.Core;

public static class SourcePathIdentity
{
    public static StringComparer CurrentComparer => GetComparer(OperatingSystem.IsWindows());

    public static StringComparer GetComparer(bool isWindows) =>
        isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
