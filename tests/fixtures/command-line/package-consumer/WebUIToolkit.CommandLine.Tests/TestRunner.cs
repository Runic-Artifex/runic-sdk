namespace WebUIToolkit.CommandLine.Tests;

internal sealed record TestCase(string Name, Func<ValueTask> Body);

internal static class TestRunner
{
    public static async Task<int> RunAsync(params IReadOnlyList<TestCase>[] suites)
    {
        int passed = 0;
        int failed = 0;

        foreach (TestCase test in suites.SelectMany(static suite => suite))
        {
            try
            {
                await test.Body();
                Console.WriteLine($"PASS {test.Name}");
                passed++;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
                failed++;
            }
        }

        Console.WriteLine($"SUMMARY passed={passed} failed={failed} total={passed + failed}");
        return failed == 0 ? 0 : 1;
    }
}

internal static class AssertEx
{
    public static void True(bool condition, string message = "Expected condition to be true.")
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(message ?? $"Expected <{expected}> but found <{actual}>.");
        }
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? message = null)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(message ?? $"Expected [{string.Join(", ", expected)}] but found [{string.Join(", ", actual)}].");
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

    public static T Throws<T>(Func<object?> action) where T : Exception
    {
        try
        {
            _ = action();
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
            await action();
        }
        catch (T exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
