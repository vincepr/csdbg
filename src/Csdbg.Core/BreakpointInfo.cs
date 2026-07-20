namespace Csdbg.Core;

public sealed class BreakpointInfo
{
    public string Id { get; set; } = "";
    public string File { get; set; } = "";
    public int RequestedLine { get; set; }
    public int Line { get; set; }
    public string? Condition { get; set; }
    public bool Verified { get; set; }
    public int? AdapterId { get; set; }
    public string? Message { get; set; }
}
