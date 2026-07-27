namespace Csdbg.Core;

public sealed record EvaluationRisk(bool RequiresUnsafe, string Reason);

public static class EvaluationSafetyPolicy
{
    public static EvaluationRisk Classify(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var trimmed = expression.Trim();
        var mutationCode = GetCodeForMutationAnalysis(trimmed);
        if (mutationCode.Contains("++", StringComparison.Ordinal) ||
            mutationCode.Contains("--", StringComparison.Ordinal))
        {
            return new EvaluationRisk(true, "increment or decrement can mutate program state");
        }

        if (ContainsAssignmentOperator(mutationCode))
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
            var beforePrevious = index > 1 ? expression[index - 2] : '\0';
            var next = index + 1 < expression.Length ? expression[index + 1] : '\0';
            if (previous is '=' or '!' ||
                (previous is '<' or '>' && beforePrevious != previous) ||
                next is '=' or '>')
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static string GetCodeForMutationAnalysis(string expression)
    {
        var result = new System.Text.StringBuilder(expression.Length);
        var index = 0;
        AppendCode(expression, ref index, result, stopAtInterpolationEnd: false);
        return result.ToString();
    }

    private static void AppendCode(
        string expression,
        ref int index,
        System.Text.StringBuilder result,
        bool stopAtInterpolationEnd)
    {
        var nestedBraceDepth = 0;
        while (index < expression.Length)
        {
            if (TrySkipComment(expression, ref index, result))
            {
                continue;
            }

            if (TrySkipLiteral(expression, ref index, result))
            {
                continue;
            }

            var current = expression[index];
            if (stopAtInterpolationEnd)
            {
                if (current == '{')
                {
                    nestedBraceDepth++;
                }
                else if (current == '}')
                {
                    if (nestedBraceDepth == 0)
                    {
                        index++;
                        return;
                    }

                    nestedBraceDepth--;
                }
            }

            result.Append(current);
            index++;
        }
    }

    private static bool TrySkipComment(
        string expression,
        ref int index,
        System.Text.StringBuilder result)
    {
        if (StartsWith(expression, index, "//"))
        {
            index += 2;
            while (index < expression.Length && expression[index] is not '\r' and not '\n')
            {
                index++;
            }

            result.Append(' ');
            return true;
        }

        if (!StartsWith(expression, index, "/*"))
        {
            return false;
        }

        index += 2;
        while (index < expression.Length && !StartsWith(expression, index, "*/"))
        {
            index++;
        }

        index = Math.Min(index + 2, expression.Length);
        result.Append(' ');
        return true;
    }

    private static bool TrySkipLiteral(
        string expression,
        ref int index,
        System.Text.StringBuilder result)
    {
        if (StartsWith(expression, index, "\"\"\""))
        {
            result.Append(expression.AsSpan(index));
            index = expression.Length;
            return true;
        }

        if (StartsWith(expression, index, "$@\"") || StartsWith(expression, index, "@$\""))
        {
            SkipString(expression, ref index, result, openingLength: 3, verbatim: true, interpolated: true);
            return true;
        }

        if (StartsWith(expression, index, "$\""))
        {
            SkipString(expression, ref index, result, openingLength: 2, verbatim: false, interpolated: true);
            return true;
        }

        if (StartsWith(expression, index, "@\""))
        {
            SkipString(expression, ref index, result, openingLength: 2, verbatim: true, interpolated: false);
            return true;
        }

        if (expression[index] == '"')
        {
            SkipString(expression, ref index, result, openingLength: 1, verbatim: false, interpolated: false);
            return true;
        }

        if (expression[index] == '\'')
        {
            SkipCharacter(expression, ref index, result);
            return true;
        }

        return false;
    }

    private static void SkipString(
        string expression,
        ref int index,
        System.Text.StringBuilder result,
        int openingLength,
        bool verbatim,
        bool interpolated)
    {
        var literalStart = index;
        var resultStart = result.Length;
        index += openingLength;
        result.Append(' ');

        while (index < expression.Length)
        {
            if (!verbatim && expression[index] == '\\')
            {
                index += Math.Min(2, expression.Length - index);
                continue;
            }

            if (expression[index] == '"')
            {
                if (verbatim && index + 1 < expression.Length && expression[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                index++;
                return;
            }

            if (interpolated && expression[index] == '{')
            {
                if (index + 1 < expression.Length && expression[index + 1] == '{')
                {
                    index += 2;
                    continue;
                }

                index++;
                AppendCode(expression, ref index, result, stopAtInterpolationEnd: true);
                continue;
            }

            if (interpolated &&
                expression[index] == '}' &&
                index + 1 < expression.Length &&
                expression[index + 1] == '}')
            {
                index += 2;
                continue;
            }

            index++;
        }

        result.Length = resultStart;
        result.Append(expression.AsSpan(literalStart));
    }

    private static void SkipCharacter(
        string expression,
        ref int index,
        System.Text.StringBuilder result)
    {
        var literalStart = index;
        var resultStart = result.Length;
        result.Append(' ');
        index++;
        while (index < expression.Length)
        {
            if (expression[index] == '\\')
            {
                index += Math.Min(2, expression.Length - index);
                continue;
            }

            if (expression[index++] == '\'')
            {
                return;
            }
        }

        result.Length = resultStart;
        result.Append(expression.AsSpan(literalStart));
    }

    private static bool StartsWith(string value, int startIndex, string candidate)
    {
        return value.AsSpan(startIndex).StartsWith(candidate, StringComparison.Ordinal);
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
