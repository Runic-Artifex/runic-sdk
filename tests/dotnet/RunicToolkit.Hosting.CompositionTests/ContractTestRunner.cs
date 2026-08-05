using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace RunicToolkit.Hosting.CompositionTests;

internal sealed record ContractScenario(string Id, Func<ValueTask> ExecuteAsync);

internal static class ContractTestRunner
{
    public static async Task<int> RunAsync(IReadOnlyList<ContractScenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);

        var failures = new List<(string Id, Exception Error)>();
        Console.WriteLine($"1..{scenarios.Count}");

        for (var index = 0; index < scenarios.Count; index++)
        {
            ContractScenario scenario = scenarios[index];
            try
            {
                await scenario.ExecuteAsync().ConfigureAwait(false);
                Console.WriteLine($"ok {index + 1} - {scenario.Id}");
            }
            catch (Exception exception) when (!IsProcessFatal(exception))
            {
                failures.Add((scenario.Id, exception));
                Console.WriteLine($"not ok {index + 1} - {scenario.Id}");
                Console.WriteLine($"  {exception.GetType().Name}: {exception.Message}");
            }
        }

        Console.WriteLine($"# {scenarios.Count - failures.Count} passed; {failures.Count} failed");
        return failures.Count == 0 ? 0 : 1;
    }

    private static bool IsProcessFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}

internal sealed class ContractAssertionException(string message) : Exception(message);

internal static class ContractAssert
{
    public static void Equal<T>(T expected, T actual, string? because = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Fail($"Expected <{expected}> but found <{actual}>.{Reason(because)}");
        }
    }

    public static void Same(object expected, object? actual, string? because = null)
    {
        if (!ReferenceEquals(expected, actual))
        {
            Fail($"Expected the same object reference.{Reason(because)}");
        }
    }

    public static void EqualSequence<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        string? because = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        if (expected.Count != actual.Count)
        {
            Fail($"Expected {expected.Count} values but found {actual.Count}.{Reason(because)}");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!EqualityComparer<T>.Default.Equals(expected[index], actual[index]))
            {
                Fail($"Sequences differ at index {index}: expected <{expected[index]}> but found <{actual[index]}>.{Reason(because)}");
            }
        }
    }

    public static void True(bool condition, string? because = null)
    {
        if (!condition)
        {
            Fail($"Expected condition to be true.{Reason(because)}");
        }
    }

    public static void False(bool condition, string? because = null) => True(!condition, because);

    public static T IsType<T>(object? value, string? because = null)
    {
        if (value is not T typed)
        {
            Fail($"Expected {typeof(T).FullName}, found {value?.GetType().FullName ?? "<null>"}.{Reason(because)}");
            throw new UnreachableException();
        }

        return typed;
    }

    public static TException Throws<TException>(Action action, string? because = null)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            action();
        }
        catch (TException exception) when (!IsProcessFatal(exception))
        {
            return exception;
        }
        catch (Exception exception) when (!IsProcessFatal(exception))
        {
            Fail($"Expected {typeof(TException).Name}, found {exception.GetType().Name}.{Reason(because)}");
        }

        Fail($"Expected {typeof(TException).Name}, but no exception was thrown.{Reason(because)}");
        throw new UnreachableException();
    }

    public static async ValueTask<TException> ThrowsAsync<TException>(
        Func<ValueTask> action,
        string? because = null)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException exception) when (!IsProcessFatal(exception))
        {
            return exception;
        }
        catch (Exception exception) when (!IsProcessFatal(exception))
        {
            Fail($"Expected {typeof(TException).Name}, found {exception.GetType().Name}.{Reason(because)}");
        }

        Fail($"Expected {typeof(TException).Name}, but no exception was thrown.{Reason(because)}");
        throw new UnreachableException();
    }

    private static string Reason(string? because) => because is null ? string.Empty : $" Because {because}";

    private static void Fail(string message) => throw new ContractAssertionException(message);

    private static bool IsProcessFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
