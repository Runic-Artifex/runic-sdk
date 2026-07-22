using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebUIToolkit.DependencyNotices.Npm.Tests;

internal sealed class TestHarness
{
    private readonly List<(string Name, Action Test)> _tests = [];

    public void Add(string name, Action test) => _tests.Add((name, test));

    public ValueTask<int> RunAsync()
    {
        int failures = 0;
        foreach ((string name, Action test) in _tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
            }
        }

        Console.WriteLine($"Executed {_tests.Count} tests; {failures} failed.");
        return ValueTask.FromResult(failures == 0 ? 0 : 1);
    }
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
        }
    }

    public static void True(bool value, string? message = null)
    {
        if (!value)
        {
            throw new InvalidOperationException(message ?? "Expected true.");
        }
    }

    public static T Single<T>(IEnumerable<T> values)
    {
        T[] items = values.ToArray();
        Equal(1, items.Length);
        return items[0];
    }
}
