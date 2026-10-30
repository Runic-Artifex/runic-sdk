using TUnit.Core;

namespace Runic.Desktop.Tests;

public sealed class BrowserTestProfileTests
{
    [Test]
    public async Task DeletesNestedProfileAndAcceptsAnAlreadyRemovedProfile()
    {
        var profile = Directory.CreateTempSubdirectory("runic-profile-cleanup-");
        try
        {
            var data = Directory.CreateDirectory(Path.Combine(profile.FullName, "Default"));
            File.WriteAllText(Path.Combine(data.FullName, "Account Web Data"), "test data");
            await BrowserTestProfile.DeleteAsync(profile.FullName, TimeSpan.FromSeconds(5));
            Assert.False(Directory.Exists(profile.FullName));
            await BrowserTestProfile.DeleteAsync(profile.FullName, TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (Directory.Exists(profile.FullName)) profile.Delete(recursive: true);
        }
    }

    [Test, WindowsFileLock]
    public async Task WaitsForAWindowsProfileFileToBeReleased()
    {
        var profile = Directory.CreateTempSubdirectory("runic-profile-cleanup-");
        try
        {
            Task cleanup;
            using (var locked = new FileStream(Path.Combine(profile.FullName, "Account Web Data"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                cleanup = BrowserTestProfile.DeleteAsync(profile.FullName, TimeSpan.FromSeconds(5));
                // The first deletion attempt runs synchronously before the retry delay.
                Assert.False(cleanup.IsCompleted);
                Assert.True(Directory.Exists(profile.FullName));
            }
            await cleanup.WaitScaledAsync(TimeSpan.FromSeconds(10));
            Assert.False(Directory.Exists(profile.FullName));
        }
        finally
        {
            if (Directory.Exists(profile.FullName)) profile.Delete(recursive: true);
        }
    }

    [Test, WindowsFileLock]
    public async Task PersistentWindowsLockStillFailsCleanupWithinTheDeadline()
    {
        var profile = Directory.CreateTempSubdirectory("runic-profile-cleanup-");
        try
        {
            using var locked = new FileStream(Path.Combine(profile.FullName, "Account Web Data"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            await Assert.ThrowsAsync<IOException>(() => BrowserTestProfile
                .DeleteAsync(profile.FullName, TimeSpan.FromMilliseconds(100))
                .WaitScaledAsync(TimeSpan.FromSeconds(5)));
            Assert.True(Directory.Exists(profile.FullName));
        }
        finally
        {
            if (Directory.Exists(profile.FullName)) profile.Delete(recursive: true);
        }
    }
}

// Skips a case outside Windows, which has no mandatory file-sharing locks.
public sealed class WindowsFileLockAttribute() : SkipAttribute("Requires Windows mandatory file-sharing locks.")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) => Task.FromResult(!OperatingSystem.IsWindows());
}
