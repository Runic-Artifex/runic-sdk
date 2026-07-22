using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebUIToolkit.DependencyNotices.Security.Tests;

internal sealed class TestHarness
{
    private readonly List<(string Name, Func<ValueTask> Test)> _tests = [];

    public void Add(string name, Action test) =>
        _tests.Add((name, () =>
        {
            test();
            return ValueTask.CompletedTask;
        }));

    public void Add(string name, Func<ValueTask> test) => _tests.Add((name, test));

    public async ValueTask<int> RunAsync()
    {
        int failures = 0;
        foreach ((string name, Func<ValueTask> test) in _tests)
        {
            try
            {
                await test().ConfigureAwait(false);
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        Console.WriteLine($"Executed {_tests.Count} security tests; {failures} failed.");
        return failures == 0 ? 0 : 1;
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

    public static void False(bool value, string? message = null) => True(!value, message ?? "Expected false.");

    public static void Contains(string expected, string actual, StringComparison comparison = StringComparison.Ordinal)
    {
        if (!actual.Contains(expected, comparison))
        {
            throw new InvalidOperationException($"Expected text to contain '{expected}'.");
        }
    }

    public static void DoesNotContain(string unexpected, string actual, StringComparison comparison = StringComparison.Ordinal)
    {
        if (actual.Contains(unexpected, comparison))
        {
            throw new InvalidOperationException($"Expected text not to contain '{unexpected}'.");
        }
    }

    public static T Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    public static async ValueTask<T> ThrowsAsync<T>(Func<ValueTask> action) where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (T exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
