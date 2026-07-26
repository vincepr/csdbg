namespace Csdbg.Mcp.Tests;

public sealed class SkillTests
{
    [Fact]
    [Trait("Description", "Focused evaluation avoids mandatory DAP traversal.")]
    public async Task CsdbgSkillPrefersFocusedEvaluationWithoutMandatoryTraversal()
    {
        var skillPath = Path.Combine(
            AppContext.BaseDirectory,
            "skills",
            "csdbg-debug",
            "SKILL.md");

        var skill = await File.ReadAllTextAsync(skillPath);

        Assert.Contains("prefer focused `evaluate_expression`", skill, StringComparison.Ordinal);
        Assert.Contains(
            "insufficient or a different frame is needed",
            skill,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Inspect from broad to narrow", skill, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "`get_threads` -> `get_call_stack`",
            skill,
            StringComparison.Ordinal);
    }
}
