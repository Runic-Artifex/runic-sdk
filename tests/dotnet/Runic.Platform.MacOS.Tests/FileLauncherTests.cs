using TUnit.Core;

namespace Runic.Platform.MacOS.Tests;

// Open refuses names LaunchServices would run (MacDesktopFileLauncher.StartsProgram). The block list is portable.
internal sealed class FileLauncherTests
{
    [Test, DisplayName("file launcher: Open refuses applications, scripts, installers, plug-ins and disk images")]
    public void BlockList()
    {
        // StartsProgram refuses a name it cannot resolve, so each name is an existing file.
        var root = Directory.CreateTempSubdirectory("runic-open-");
        try
        {
            foreach (string extension in new[]
            {
                ".app", ".command", ".workflow", ".pkg", ".mobileconfig", ".webloc", ".jnlp", ".shortcut", ".dmg", ".iso", ".img", ".cdr", ".sparseimage",
                ".sparsebundle", ".osax", ".kext", ".plugin", ".bundle", ".appex", ".xpc", ".DMG",
            })
                Check(MacDesktopFileLauncher.StartsProgram(File(root, "file" + extension)), $"{extension} is refused");
            foreach (string document in new[] { "notes.txt", "report.pdf", "photo.png", "archive.zip" })
                Check(!MacDesktopFileLauncher.StartsProgram(File(root, document)), $"{document} is opened");
        }
        finally { root.Delete(recursive: true); }
    }

    private static string File(DirectoryInfo root, string name)
    {
        string path = Path.Combine(root.FullName, name);
        System.IO.File.WriteAllText(path, "");
        return path;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
