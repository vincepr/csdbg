namespace Csdbg.Core.Tests;

public sealed class EvaluationSafetyPolicyTests
{
    [Theory]
    [InlineData("counter", "read-oriented expression")]
    [InlineData("customer.Name", "member access may invoke a property getter")]
    [InlineData("items[0].Value", "member access may invoke a property getter")]
    public void Classify_ReadOnlyExpressions_DoesNotRequireUnsafe(
        string expression,
        string expectedReason)
    {
        var risk = EvaluationSafetyPolicy.Classify(expression);

        Assert.False(risk.RequiresUnsafe);
        Assert.Equal(expectedReason, risk.Reason);
    }

    [Theory]
    [InlineData("counter = 1")]
    [InlineData("counter += 1")]
    [InlineData("customer.Name = value")]
    public void Classify_Assignments_RequiresUnsafe(string expression)
    {
        var risk = EvaluationSafetyPolicy.Classify(expression);

        Assert.True(risk.RequiresUnsafe);
        Assert.Equal("assignment can mutate program state", risk.Reason);
    }

    [Theory]
    [InlineData("left == right")]
    [InlineData("left != right")]
    [InlineData("left <= right")]
    [InlineData("left >= right")]
    public void Classify_Comparisons_DoesNotTreatEqualityAsAssignment(string expression)
    {
        var risk = EvaluationSafetyPolicy.Classify(expression);

        Assert.False(risk.RequiresUnsafe);
        Assert.Equal("read-oriented expression", risk.Reason);
    }

    [Theory]
    [InlineData("counter++")]
    [InlineData("--counter")]
    public void Classify_IncrementsAndDecrements_RequiresUnsafe(string expression)
    {
        var risk = EvaluationSafetyPolicy.Classify(expression);

        Assert.True(risk.RequiresUnsafe);
        Assert.Equal("increment or decrement can mutate program state", risk.Reason);
    }

    [Theory]
    [InlineData("Refresh()")]
    [InlineData("service.Refresh()")]
    [InlineData("Calculate (value)")]
    public void Classify_MethodCalls_RequiresUnsafe(string expression)
    {
        var risk = EvaluationSafetyPolicy.Classify(expression);

        Assert.True(risk.RequiresUnsafe);
        Assert.Equal("method calls can execute user code", risk.Reason);
    }
}
