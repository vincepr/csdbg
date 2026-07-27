namespace Csdbg.Core;

public enum EvaluationErrorKind
{
    Failed,
    StaleFrame,
    Timeout
}

public sealed class EvaluationException : Exception
{
    private const int BackendDetailLimit = 512;

    private EvaluationException(
        EvaluationErrorKind kind,
        string message,
        string? backendDetail,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        BackendDetail = BoundDetail(backendDetail);
    }

    public EvaluationErrorKind Kind { get; }
    public string? BackendDetail { get; }

    public static EvaluationException Failed(string? backendDetail) =>
        new(
            EvaluationErrorKind.Failed,
            "The debugger could not classify the evaluation failure.",
            backendDetail);

    public static EvaluationException Timeout(TimeoutException innerException) =>
        new(
            EvaluationErrorKind.Timeout,
            "Expression evaluation timed out.",
            backendDetail: null,
            innerException);

    public static EvaluationException StaleFrame() =>
        new(
            EvaluationErrorKind.StaleFrame,
            "The requested stack frame is no longer available. Refresh the call stack and retry.",
            backendDetail: null);

    private static string? BoundDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var normalized = detail.Trim();
        return normalized.Length <= BackendDetailLimit
            ? normalized
            : $"{normalized[..(BackendDetailLimit - 1)]}…";
    }
}
