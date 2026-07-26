namespace SchedulerReplay;

internal static class TaskRunner
{
    public static IReadOnlyList<string> RunTasks(IReadOnlyList<ScheduledTask> tasks)
    {
        var order = TaskResolver.ResolveOrder(tasks);
        var results = new List<string>(order.Count);

        foreach (var task in order)
        {
            Console.WriteLine($"Running: {task.Name} (priority={task.Priority})");
            results.Add(task.Name);
        }

        return results;
    }
}
