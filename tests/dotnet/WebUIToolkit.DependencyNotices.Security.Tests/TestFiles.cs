using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WebUIToolkit.DependencyNotices.Security.Tests;

internal static class TestFiles
{
    public static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "security", name);

    public static JsonDocument ReadFixture(string name) => JsonDocument.Parse(File.ReadAllBytes(Fixture(name)));

    public static void WithTemporaryDirectory(Action<string> action)
    {
        string path = CreateTemporaryDirectory();
        try
        {
            action(path);
        }
        finally
        {
            DeleteTree(path);
        }
    }

    public static async ValueTask WithTemporaryDirectoryAsync(Func<string, ValueTask> action)
    {
        string path = CreateTemporaryDirectory();
        try
        {
            await action(path).ConfigureAwait(false);
        }
        finally
        {
            DeleteTree(path);
        }
    }

    public static void WriteUtf8(string path, string text) =>
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "WebUIToolkit.DependencyNotices.Security.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(entry, FileAttributes.Normal);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Directory.Delete(path, recursive: true);
    }
}
