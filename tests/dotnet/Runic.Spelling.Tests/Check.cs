namespace Runic.Spelling.Tests;

// The suite's assertions.
internal static class Check
{
    internal static void Equal<T>(T expected, T actual, string? because = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {Show(expected)}, got {Show(actual)}{(because is null ? "" : $": {because}")}.");
    }

    internal static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? because = null)
    {
        T[] wanted = [.. expected];
        T[] got = [.. actual];
        if (!wanted.SequenceEqual(got))
            throw new InvalidOperationException($"Expected [{string.Join(", ", wanted.Select(Show))}], got [{string.Join(", ", got.Select(Show))}]{(because is null ? "" : $": {because}")}.");
    }

    internal static void True(bool condition, string because)
    {
        if (!condition) throw new InvalidOperationException($"Expected: {because}.");
    }

    internal static TException Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException error)
        {
            return error;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    internal static async Task<TException> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException error)
        {
            return error;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static string Show<T>(T value) => value is null ? "null" : value is string text ? $"\"{text}\"" : value.ToString() ?? "";
}

// A temporary folder, removed with the case.
internal sealed class TemporaryFolder : IDisposable
{
    internal TemporaryFolder() => Directory.CreateDirectory(Path);

    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "runic-spelling-tests-" + Guid.NewGuid().ToString("N"));

    // A folder below this one, created.
    internal string Folder(params string[] parts)
    {
        var folder = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    // A fake dictionary: an empty affix file and a word list, enough to be found; Real copies a bundled one.
    internal static void Dictionary(string folder, string name, params string[] words)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(System.IO.Path.Combine(folder, name + ".aff"), "SET UTF-8\n");
        File.WriteAllLines(System.IO.Path.Combine(folder, name + ".dic"), [words.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), .. words]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Environments
{
    // An environment with only the variables given, a home in the temporary folder, the program in its app folder, and the
    // fixed system folders (/usr/share, /Library/Spelling) under its system folder, so that no test reads the dictionaries
    // of the computer it runs on (an Ubuntu runner has /usr/share/hunspell/en_US).
    internal static SpellingEnvironment Fake(SpellingPlatform platform, string root, params (string Name, string Value)[] variables)
    {
        var values = variables.ToDictionary(variable => variable.Name, variable => variable.Value, StringComparer.Ordinal);
        return new(platform, name => values.GetValueOrDefault(name), Path.Combine(root, "home"), Path.Combine(root, "app"),
            Path.Combine(root, "system"));
    }

    // The bundled dictionaries in this suite's output, where the package's targets copy them.
    internal static string Bundled => Path.Combine(AppContext.BaseDirectory, "dictionaries");
}
