using System;
using System.Collections.Generic;
using System.Globalization;

namespace WebUIToolkit.MVVM.Build.Tests;

internal sealed class TestRunner
{
    private readonly List<(string Name, Action Body)> tests = [];

    public void Add(string name, Action body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(body);
        tests.Add((name, body));
    }

    public int Run()
    {
        int failures = 0;
        foreach ((string name, Action body) in tests)
        {
            try
            {
                body();
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"PASS {name}"));
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"FAIL {name}"));
                Console.Error.WriteLine(exception);
            }
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"TOTAL {tests.Count} PASSED {tests.Count - failures} FAILED {failures}"));
        return failures == 0 ? 0 : 1;
    }
}

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{(message is null ? string.Empty : message + ": ")}Expected <{expected}>; actual <{actual}>."));
        }
    }

    public static void Contains(string expectedSubstring, string actual, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(expectedSubstring);
        ArgumentNullException.ThrowIfNull(actual);
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                message ?? string.Create(CultureInfo.InvariantCulture, $"Expected <{actual}> to contain <{expectedSubstring}>."));
        }
    }

    public static T Single<T>(IReadOnlyList<T> items, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count != 1)
        {
            throw new InvalidOperationException(
                message ?? string.Create(CultureInfo.InvariantCulture, $"Expected one item; actual count was {items.Count}."));
        }

        return items[0];
    }
}
