using System;
using System.Threading;

public class SharedDependency
{
    private int increment;

    public void Called(string from)
    {
        var newValue = Interlocked.Increment(ref increment);
        Console.WriteLine($"Called '{@from}'. New value is '{newValue}'");
    }
}