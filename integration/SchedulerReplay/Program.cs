using SchedulerReplay;

if (args is ["--adapter-owned-target"])
{
    Console.WriteLine("ready:target");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

var tasks = new[]
{
    new ScheduledTask("deploy", 10, ["build", "test"]),
    new ScheduledTask("build", 8),
    new ScheduledTask("test", 9, ["build"]),
    new ScheduledTask("lint", 2),
    new ScheduledTask("docs", 1)
};

var actual = TaskRunner.RunTasks(tasks);
var expected = new[] { "build", "test", "deploy", "lint", "docs" };

if (!actual.SequenceEqual(expected))
{
    Console.WriteLine($"Expected: {string.Join(", ", expected)}");
    Console.WriteLine($"Actual:   {string.Join(", ", actual)}");
}
