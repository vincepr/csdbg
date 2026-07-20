namespace Csdbg.Core;

public sealed record EvaluationRisk(bool RequiresUnsafe, string Reason);

public static class EvaluationSafetyPolicy
{
    public static EvaluationRisk Classify(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var trimmed = expression.Trim();
        if (trimmed.Contains("++", StringComparison.Ordinal) ||
            trimmed.Contains("--", StringComparison.Ordinal))
        {
            return new EvaluationRisk(true, "increment or decrement can mutate program state");
        }

        if (ContainsAssignmentOperator(trimmed))
        {
            return new EvaluationRisk(true, "assignment can mutate program state");
        }

        if (LooksLikeMethodCall(trimmed))
        {
            return new EvaluationRisk(true, "method calls can execute user code");
        }

        var reason = trimmed.Contains('.', StringComparison.Ordinal)
            ? "member access may invoke a property getter"
            : "read-oriented expression";
        return new EvaluationRisk(false, reason);
    }

    private static bool ContainsAssignmentOperator(string expression)
    {
        for (var index = 0; index < expression.Length; index++)
        {
            if (expression[index] != '=')
            {
                continue;
            }

            var previous = index > 0 ? expression[index - 1] : '\0';
            var next = index + 1 < expression.Length ? expression[index + 1] : '\0';
            if (previous is '=' or '!' or '<' or '>' || next == '=')
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool LooksLikeMethodCall(string expression)
    {
        for (var index = 0; index < expression.Length; index++)
        {
            if (expression[index] != '(')
            {
                continue;
            }

            var previous = PreviousNonWhitespace(expression, index - 1);
            if (previous is not null &&
                (char.IsLetterOrDigit(previous.Value) || previous.Value is '_' or '>'))
            {
                return true;
            }
        }

        return false;
    }

    private static char? PreviousNonWhitespace(string value, int startIndex)
    {
        for (var index = startIndex; index >= 0; index--)
        {
            if (!char.IsWhiteSpace(value[index]))
            {
                return value[index];
            }
        }

        return null;
    }
}
