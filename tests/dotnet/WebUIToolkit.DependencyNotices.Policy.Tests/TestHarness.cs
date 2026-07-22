using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebUIToolkit.DependencyNotices.Policy.Tests;

internal sealed class TestHarness
{
    private readonly List<(string Name, Action Test)> tests = [];

    public void Add(string name, Action test) => tests.Add((name, test));

    public Task<int> RunAsync()
    {
        int failures = 0;
        foreach ((string name, Action test) in tests)
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

        Console.WriteLine($"Policy tests: {tests.Count - failures} passed, {failures} failed, {tests.Count} total.");
        return Task.FromResult(failures == 0 ? 0 : 1);
    }
}

internal static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message ?? "Expected true.");
        }
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
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
}
