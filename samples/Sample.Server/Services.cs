using Sample.Contracts;

namespace Sample.Server;

public sealed class Calculator : ICalculator
{
    public int Add(int a, int b) => a + b;

    public double Add(double a, double b) => a + b;

    public async Task<double> DivideAsync(double a, double b)
    {
        await Task.Delay(50);
        if (b == 0)
        {
            throw new DivideByZeroException("Dělení nulou.");
        }

        return a / b;
    }
}

public sealed class Counter : ICounter, IDisposable
{
    private static int _instances;
    private int _value;

    public Counter() => Console.WriteLine($"  [server] Counter instance #{Interlocked.Increment(ref _instances)} created");

    public int Increment() => Interlocked.Increment(ref _value);

    public void Dispose() => Console.WriteLine("  [server] Counter disposed");
}

public sealed class Greeter : IGreeter
{
    public string Greet(Person person) => $"Ahoj {person.Name}, je ti {person.Age} let.";

    public Task<IReadOnlyList<Person>> GetAdultsAsync(IReadOnlyList<Person> people) =>
        Task.FromResult<IReadOnlyList<Person>>(people.Where(p => p.Age >= 18).ToList());
}
