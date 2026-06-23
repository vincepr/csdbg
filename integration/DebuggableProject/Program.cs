using System.Diagnostics;

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "once";

Console.WriteLine($"SmokeApp pid={Environment.ProcessId} mode={mode}");

return mode switch
{
    "once" => RunOnce(),
    "loop" => RunLoop(),
    "throw" => RunThrow(),
    _ => Fail(mode)
};

static int RunOnce()
{
    var person = BuildPerson("Ada", 37);
    var total = Sum(person.LuckyNumbers);
    var message = $"name={person.Name} age={person.Age} total={total}";

    Console.WriteLine(message);
    return 0;
}

static int RunLoop()
{
    var tick = 0;
    while (tick < 300)
    {
        var snapshot = new CounterSnapshot(
            Tick: tick,
            Doubled: tick * 2,
            IsEven: tick % 2 == 0,
            TimestampUtc: DateTime.UtcNow);

        Console.WriteLine(
            $"tick={snapshot.Tick} doubled={snapshot.Doubled} even={snapshot.IsEven}");

        tick++;
        Thread.Sleep(250);
    }

    return 0;
}

static int RunThrow()
{
    var order = new Order("A-100", 0);
    ValidateOrder(order);
    return 0;
}

static Person BuildPerson(string name, int age)
{
    var luckyNumbers = new[] { 3, 5, 8, 13 };
    return new Person(name, age, luckyNumbers);
}

static int Sum(int[] numbers)
{
    var total = 0;
    foreach (var number in numbers)
    {
        total += number;
    }

    return total;
}

static void ValidateOrder(Order order)
{
    if (order.Quantity <= 0)
    {
        throw new InvalidOperationException($"Invalid quantity for order {order.Id}");
    }
}

static int Fail(string mode)
{
    Console.Error.WriteLine($"Unknown mode: {mode}");
    return 2;
}

internal sealed record Person(string Name, int Age, int[] LuckyNumbers);
internal sealed record CounterSnapshot(int Tick, int Doubled, bool IsEven, DateTime TimestampUtc);
internal sealed record Order(string Id, int Quantity);
