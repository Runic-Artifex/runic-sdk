using System.Security.AccessControl;
using System.Security.Principal;
using TUnit.Core;
using static Runic.Spelling.Tests.Check;

namespace Runic.Spelling.Tests;

// DEV-SEC-88: the personal word list is readable by its user only: 0600 on Unix, an ACL of the current user alone on
// Windows. Each write replaces the file at once, so a list that another mode left behind gets that mode at the next
// write, and the directories the package creates for it are 0700 on Unix.
public sealed class PrivateFileTests
{
    [Test]
    public async Task TheWordListIsTheUsersAlone()
    {
        using var temporary = new TemporaryFolder();
        var path = Path.Combine(temporary.Path, "runic", "user-words.txt");
        var words = await SpellingUserWords.OpenAsync(path);
        True(await words.AddAsync("brinewise"), "brinewise is added");
        True(await words.AddAsync("glimmerfen"), "glimmerfen is added");
        RequireOwnerOnly(path);
        Equal($"brinewise{Environment.NewLine}glimmerfen{Environment.NewLine}", File.ReadAllText(path));
        // No temporary file stays beside it.
        Sequence(["user-words.txt"], Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!).Select(Path.GetFileName).OfType<string>());
        if (!OperatingSystem.IsWindows())
            Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
    }

    [Test]
    public async Task AWriteMakesAnOpenListTheUsersAlone()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryFolder();
        var path = Path.Combine(temporary.Path, "user-words.txt");
        File.WriteAllText(path, "saltwort\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var words = await SpellingUserWords.OpenAsync(path);
        True(await words.AddAsync("brinewise"), "brinewise is added");
        RequireOwnerOnly(path);
        Sequence(["brinewise", "saltwort"], (await SpellingUserWords.OpenAsync(path)).Words.Order(StringComparer.Ordinal));
    }

    [Test]
    public async Task TheDownloadDirectoryIsCreatedForItsUserAlone()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryFolder();
        await using var server = await DictionaryServer.StartAsync();
        var dictionaries = new SpellingDictionaries(new()
        {
            Environment = Environments.Fake(SpellingPlatform.Linux, temporary.Path),
            UseSystemDictionaries = false,
            ApplicationDirectories = [],
            Catalog = server.Catalog("de_CH", "de-CH"),
            AllowLoopbackDownloads = true,
        });
        await dictionaries.DownloadAsync(dictionaries.Catalog.Find("de_CH")!);
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Equal(Private, File.GetUnixFileMode(dictionaries.DownloadDirectory));
        Equal(Private, File.GetUnixFileMode(Path.GetDirectoryName(dictionaries.DownloadDirectory)!));
    }

    // In the WindowsPrivateFiles category, which the native job's Windows x64 leg runs: the leg that PrivateFiles.cs and
    // SpellingUserWords.cs plan (eng/ci/platforms.mjs).
    [Test, Category("WindowsPrivateFiles"), DisplayName("on Windows, the word list grants only its user, also after a rewrite")]
    public async Task TheWordListIsPrivateOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryFolder();
        var path = Path.Combine(temporary.Path, "Runic", "user-words.txt");
        var words = await SpellingUserWords.OpenAsync(path);
        True(await words.AddAsync("brinewise"), "brinewise is added");
        RequireOwnerOnly(path);
        // Each write replaces the list with a new file; the replacement grants only the user too.
        True(await words.RemoveAsync("brinewise"), "brinewise is removed");
        True(await words.AddAsync("glimmerfen"), "glimmerfen is added");
        RequireOwnerOnly(path);
        Sequence(["glimmerfen"], (await SpellingUserWords.OpenAsync(path)).Words);
        Sequence(["user-words.txt"], Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!).Select(Path.GetFileName).OfType<string>());
    }

    private static void RequireOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RequireOwnerOnlyOnWindows(path);
            return;
        }
        Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    // The file's access list: one rule of its own, full control for the current user, and nothing inherited.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RequireOwnerOnlyOnWindows(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        True(security.AreAccessRulesProtected, "the list inherits no access rules");
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Equal(1, rules.Length, $"one access rule, not {string.Join(", ", rules.Select(rule => $"{rule.IdentityReference.Value} {rule.AccessControlType} {rule.FileSystemRights}"))}");
        Equal(WindowsIdentity.GetCurrent().User!.Value, rules[0].IdentityReference.Value);
        Equal(AccessControlType.Allow, rules[0].AccessControlType);
        Equal(FileSystemRights.FullControl, rules[0].FileSystemRights & FileSystemRights.FullControl);
        True(!rules[0].IsInherited, "the user's rule is the file's own");
    }
}
