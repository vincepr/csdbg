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

    [Fact]
    public void Classify_AssignmentCharacterInsideStringLiteral_DoesNotRequireUnsafe()
    {
        const string expression =
            "task.Name + \"<-\" + task.Dependencies[0] + \"|resolved=\" + resolved[0].Name";

        var risk = EvaluationSafetyPolicy.Classify(expression);

        Assert.False(risk.RequiresUnsafe);
        Assert.Equal("member access may invoke a property getter", risk.Reason);
    }

    [Theory]
    [InlineData("value + \"label=\\\"ready\\\"\"")]
    [InlineData("value + @\"C:\\logs\\key=value\"")]
    [InlineData("$\"label={value}=ready\"")]
    [InlineData("\"counter++\"")]
    [InlineData("value + '='")]
    public void Classify_MutationCharactersInsideLiteral_DoesNotRequireUnsafe(string expression)
    {
        var risk = EvaluationSafetyPolicy.Classify(expression);

        Assert.False(risk.RequiresUnsafe);
        Assert.Equal("read-oriented expression", risk.Reason);
    }

    [Theory]
    [InlineData("\"label=\" + (counter = 1)", "assignment can mutate program state")]
    [InlineData("$\"label={counter = 1}\"", "assignment can mutate program state")]
    [InlineData("$@\"label={counter += 1}\"", "assignment can mutate program state")]
    [InlineData("@$\"label={--counter}\"", "increment or decrement can mutate program state")]
    [InlineData("\"counter++\" + counter++", "increment or decrement can mutate program state")]
    [InlineData("counter /* \"literal-looking comment\" */ = 1", "assignment can mutate program state")]
    [InlineData("counter /* \" */ = 1", "assignment can mutate program state")]
    [InlineData("counter <<= 1", "assignment can mutate program state")]
    [InlineData("counter >>= 1", "assignment can mutate program state")]
    [InlineData("counter >>>= 1", "assignment can mutate program state")]
    [InlineData("$\"value={counter <<= 1}\"", "assignment can mutate program state")]
    [InlineData("$\"value={counter >>= 1}\"", "assignment can mutate program state")]
    [InlineData("$\"value={counter >>>= 1}\"", "assignment can mutate program state")]
    public void Classify_MutationOutsideLiteral_RequiresUnsafe(
        string expression,
        string expectedReason)
    {
        var risk = EvaluationSafetyPolicy.Classify(expression);

        Assert.True(risk.RequiresUnsafe);
        Assert.Equal(expectedReason, risk.Reason);
    }

    [Theory]
    [InlineData("\"unterminated = counter = 1")]
    [InlineData("'unterminated = counter = 1")]
    public void Classify_UnterminatedLiteralWithAssignment_FailsSafe(string expression)
    {
        var risk = EvaluationSafetyPolicy.Classify(expression);

        Assert.True(risk.RequiresUnsafe);
        Assert.Equal("assignment can mutate program state", risk.Reason);
    }

    [Fact]
    public void Classify_RawStringAssignmentCharacter_RequiresUnsafeUntilSupported()
    {
        var risk = EvaluationSafetyPolicy.Classify("\"\"\"label=value\"\"\"");

        Assert.True(risk.RequiresUnsafe);
        Assert.Equal("assignment can mutate program state", risk.Reason);
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
    [InlineData("items.Where(item => item.Price > 60).Count()")]
    public void Classify_MethodCalls_RequiresUnsafe(string expression)
    {
        var risk = EvaluationSafetyPolicy.Classify(expression);

        Assert.True(risk.RequiresUnsafe);
        Assert.Equal("method calls can execute user code", risk.Reason);
    }
}
