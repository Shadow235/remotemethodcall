using RemoteMethodCall;

namespace Sample.Contracts;

public sealed record Person(string Name, int Age);

[RemoteService]
public interface ICalculator
{
    int Add(int a, int b);

    double Add(double a, double b);

    Task<double> DivideAsync(double a, double b);
}

[RemoteService]
public interface ICounter
{
    int Increment();
}

[RemoteService("greeter")]
public interface IGreeter
{
    string Greet(Person person);

    Task<IReadOnlyList<Person>> GetAdultsAsync(IReadOnlyList<Person> people);
}
