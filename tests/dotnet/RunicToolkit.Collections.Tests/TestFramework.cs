using System;
using System.Collections.Generic;
using System.Linq;

namespace RunicToolkit.Collections.Tests;

internal readonly record struct TestCase(string Name, Action Body);

internal static class TestRunner
{
    public static int Run(params IReadOnlyList<TestCase>[] suites)
    {
        var passed = 0;
        var failed = 0;
        foreach (var test in suites.SelectMany(static suite => suite))
        {
            try
            {
                test.Body();
                Console.WriteLine($"PASS {test.Name}");
                passed++;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL {test.Name}");
                Console.Error.WriteLine(exception);
                failed++;
            }
        }

        Console.WriteLine($"RESULT passed={passed} failed={failed}");
        return failed == 0 ? 0 : 1;
    }
}

internal sealed class AssertionException(string message) : Exception(message);

internal static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new AssertionException(message ?? "Expected true.");
        }
    }

    public static void False(bool condition, string? message = null) => True(!condition, message ?? "Expected false.");

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException(message ?? $"Expected <{expected}> but found <{actual}>.");
        }
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? message = null)
    {
        var expectedArray = expected.ToArray();
        var actualArray = actual.ToArray();
        if (!expectedArray.SequenceEqual(actualArray))
        {
            throw new AssertionException(message ??
                $"Expected [{string.Join(", ", expectedArray)}] but found [{string.Join(", ", actualArray)}].");
        }
    }

    public static void Same(object? expected, object? actual, string? message = null)
    {
        if (!ReferenceEquals(expected, actual))
        {
            throw new AssertionException(message ?? "Expected the same object reference.");
        }
    }

    public static TException Throws<TException>(Action action, string? messageContains = null)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            if (messageContains is not null && !exception.Message.Contains(messageContains, StringComparison.OrdinalIgnoreCase))
            {
                throw new AssertionException(
                    $"Expected {typeof(TException).Name} message to contain '{messageContains}', but it was '{exception.Message}'.");
            }

            return exception;
        }
        catch (Exception exception)
        {
            throw new AssertionException(
                $"Expected {typeof(TException).Name}, but caught {exception.GetType().Name}: {exception.Message}");
        }

        throw new AssertionException($"Expected {typeof(TException).Name}, but no exception was thrown.");
    }
}
