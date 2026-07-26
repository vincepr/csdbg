namespace SchedulerReplay;

internal sealed class ScheduledTask(
    string name,
    int priority,
    IReadOnlyList<string>? dependencies = null)
{
    public string Name { get; } = name;

    public int Priority { get; } = priority;

    public IReadOnlyList<string> Dependencies { get; } = dependencies ?? [];
}
