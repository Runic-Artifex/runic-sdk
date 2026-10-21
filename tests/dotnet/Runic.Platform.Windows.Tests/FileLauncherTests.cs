using Microsoft.Win32;
using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

// Open refuses names the shell would run (WindowsFileLauncher.StartsProgram). The block list is portable; the check of the
// type's registration runs on Windows only.
#pragma warning disable CA1416 // The native case checks for Windows first.
internal sealed class FileLauncherTests
{
    [Test, DisplayName("file launcher: Open refuses programs, scripts, installers, shortcuts and disk images")]
    public void BlockList()
    {
        // StartsProgram refuses a name it cannot resolve, so each name is an existing file.
        var root = Directory.CreateTempSubdirectory("runic-open-");
        try
        {
            foreach (string extension in new[]
            {
                ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".hta", ".msi", ".lnk", ".url", ".reg", ".appref-ms", ".settingcontent-ms",
                ".vb", ".ws", ".xll", ".jnlp", ".msu", ".rdp", ".theme", ".themepack", ".deskthemepack", ".iso", ".img", ".vhd", ".vhdx", ".xbap",
                ".gadget", ".hlp", ".ins", ".isp", ".shb", ".shs", ".sct", ".mde", ".ade", ".adp", ".accde", ".ps1xml", ".ps2", ".ps2xml", ".psc1",
                ".psc2", ".vsix", ".vsto", ".xla", ".xlam", ".VHDX",
            })
                Check(WindowsFileLauncher.StartsProgram(File(root, "file" + extension)), $"{extension} is refused");
            foreach (string document in new[] { "notes.txt", "report.pdf", "photo.png", "archive.zip", "README" })
                Check(!WindowsFileLauncher.StartsProgram(File(root, document)), $"{document} is opened");
        }
        finally { root.Delete(recursive: true); }
    }

    private static string File(DirectoryInfo root, string name)
    {
        string path = Path.Combine(root.FullName, name);
        System.IO.File.WriteAllText(path, "");
        return path;
    }

    // Test-owned types in HKEY_CURRENT_USER\Software\Classes that are not in the block list. CI only.
    [Test, Explicit, Category("NativeApplications"), DisplayName("file launcher: Open refuses types registered to run the file or marked always unsafe")]
    public void RegisteredTypes()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8)) throw new PlatformNotSupportedException("The shell's associations require Windows.");
        string unique = Guid.NewGuid().ToString("N")[..8];
        string runs = ".runicrun" + unique, unsafeType = ".runicunsafe" + unique, document = ".runicdoc" + unique;
        string[] keys = [runs, unsafeType, document, "Runic.Test.Run." + unique, "Runic.Test.Unsafe." + unique, "Runic.Test.Document." + unique];
        var root = Directory.CreateTempSubdirectory("runic-open-");
        try
        {
            using (var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
            {
                // As exefile and cmdfile are registered: the open command runs the file itself.
                Register(classes, runs, keys[3], "\"%1\" %*", editFlags: null);
                // FTA_AlwaysUnsafe, which AssocIsDangerous reports, with a harmless command.
                Register(classes, unsafeType, keys[4], "notepad.exe \"%1\"", editFlags: 0x00020000);
                Register(classes, document, keys[5], "notepad.exe \"%1\"", editFlags: null);
            }
            ApplicationsTests.AssociationsChanged();
            Check(WindowsFileLauncher.StartsProgram(File(root, "file" + runs)), "a type whose open command is \"%1\" is refused");
            Check(WindowsFileLauncher.StartsProgram(File(root, "file" + unsafeType)), "a type marked always unsafe is refused");
            Check(!WindowsFileLauncher.StartsProgram(File(root, "file" + document)), "a type opened by an application is opened");
            Check(!WindowsFileLauncher.StartsProgram(File(root, "file.runicunregistered" + unique)), "an unregistered type is opened");
        }
        finally
        {
            using (var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true))
                if (classes is not null) foreach (string key in keys) classes.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            ApplicationsTests.AssociationsChanged();
            root.Delete(recursive: true);
        }
    }

    private static void Register(RegistryKey classes, string extension, string progId, string command, int? editFlags)
    {
        using (var key = classes.CreateSubKey(extension)) key.SetValue("", progId);
        using (var key = classes.CreateSubKey(progId))
        {
            key.SetValue("", "Runic test type");
            if (editFlags is { } flags) key.SetValue("EditFlags", flags, RegistryValueKind.DWord);
        }
        using (var key = classes.CreateSubKey(progId + @"\shell\open\command")) key.SetValue("", command);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
