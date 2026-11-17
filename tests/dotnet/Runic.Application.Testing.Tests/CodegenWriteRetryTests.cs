using TUnit.Core;

namespace Runic.Application.Testing.Tests;

/// <summary>
/// WIN-09: on Windows, the generator's rename over a generated file that another process holds open without delete
/// sharing fails until that process lets go; the generator retries briefly instead of failing the build.
/// </summary>
internal sealed class CodegenWriteRetryTests
{
    private static readonly int SharingViolation = unchecked((int)0x80070020);
    private static readonly int AccessDenied = unchecked((int)0x80070005);

    [Test]
    public void ARefusedRenameOnWindowsIsRetriedUntilItSucceeds()
    {
        var waits = new List<TimeSpan>();
        var attempts = 0;
        GeneratedFile.Replace("a.tmp", "a.ts", (_, _) =>
        {
            attempts++;
            if (attempts == 1) throw new IOException("The process cannot access the file.", SharingViolation);
            if (attempts == 2) throw new UnauthorizedAccessException("Access to the path is denied.") { HResult = AccessDenied };
        }, waits.Add, windows: true);
        Require(attempts == 3, $"Expected 3 attempts, made {attempts}.");
        Require(waits.SequenceEqual(GeneratedFile.RetryDelays.Take(2)), $"Unexpected waits: {string.Join(", ", waits)}.");
    }

    [Test]
    public void ARenameThatStaysRefusedReportsTheErrorAfterTheLastRetry()
    {
        var waits = new List<TimeSpan>();
        var attempts = 0;
        var error = Catch<IOException>(() => GeneratedFile.Replace("a.tmp", "a.ts", (_, _) =>
        {
            attempts++;
            throw new IOException("The process cannot access the file.", SharingViolation);
        }, waits.Add, windows: true));
        Require(error.HResult == SharingViolation, "The last error was not reported.");
        Require(attempts == GeneratedFile.RetryDelays.Length + 1, $"Unexpected attempts: {attempts}.");
        Require(waits.Sum(wait => wait.TotalMilliseconds) < 1000, "The retries wait too long for a build.");
    }

    [Test]
    public void OtherErrorsAndOtherSystemsAreNotRetried()
    {
        var attempts = 0;
        // Not a sharing problem: the directory is gone, say.
        Catch<IOException>(() => GeneratedFile.Replace("a.tmp", "a.ts", (_, _) =>
        {
            attempts++;
            throw new DirectoryNotFoundException("Could not find a part of the path.");
        }, _ => { }, windows: true));
        Require(attempts == 1, $"A missing directory was retried {attempts - 1} times.");

        // Off Windows, an access error is a real permission problem.
        attempts = 0;
        Catch<UnauthorizedAccessException>(() => GeneratedFile.Replace("a.tmp", "a.ts", (_, _) =>
        {
            attempts++;
            throw new UnauthorizedAccessException("Access to the path is denied.") { HResult = AccessDenied };
        }, _ => { }, windows: false));
        Require(attempts == 1, $"An access error off Windows was retried {attempts - 1} times.");
    }

    [Test]
    public void AGeneratedFileIsReplacedWholeAndOnlyWhenItChanged()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"runic-codegen-write-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(directory, "generated", "client.ts");
            GeneratedFile.WriteIfChanged(path, "export const a = 1;\r\n");
            Require(File.ReadAllText(path) == "export const a = 1;\n", "The line endings were not normalized.");
            var sentinel = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(path, sentinel);
            GeneratedFile.WriteIfChanged(path, "export const a = 1;\n");
            Require(Math.Abs((File.GetLastWriteTimeUtc(path) - sentinel).TotalSeconds) < 2, "Unchanged content was written again.");
            GeneratedFile.WriteIfChanged(path, "export const a = 2;\n");
            Require(File.ReadAllText(path) == "export const a = 2;\n", "Changed content was not written.");
            Require(Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1, "A temporary file stayed behind.");

            if (OperatingSystem.IsWindows())
            {
                // An editor or a scanner holds the file open without delete sharing and lets go shortly after.
                var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var release = Task.Run(async () =>
                {
                    await Task.Delay(100).ConfigureAwait(false);
                    await holder.DisposeAsync().ConfigureAwait(false);
                });
                GeneratedFile.WriteIfChanged(path, "export const a = 3;\n");
                release.GetAwaiter().GetResult();
                Require(File.ReadAllText(path) == "export const a = 3;\n", "The held file was not replaced once released.");
                Require(Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1, "A temporary file stayed behind.");
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static T Catch<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T error)
        {
            return error;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
