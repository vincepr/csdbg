namespace SchedulerReplay;

internal static class TaskResolver
{
    public static IReadOnlyList<ScheduledTask> ResolveOrder(
        IReadOnlyList<ScheduledTask> tasks)
    {
        var taskByName = tasks.ToDictionary(task => task.Name);
        var resolved = new List<ScheduledTask>();
        var seen = new HashSet<string>();

        void Visit(ScheduledTask task)
        {
            if (!seen.Add(task.Name))
            {
                return;
            }

            foreach (var dependencyName in task.Dependencies)
            {
                Visit(taskByName[dependencyName]);
            }

            resolved.Add(task);
        }

        foreach (var task in tasks.OrderBy(task => task.Priority))
        {
            Visit(task);
        }

        return resolved;
    }
}
