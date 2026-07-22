using System;
using System.IO;
using System.Text.Json;
using WebUIToolkit.DependencyNotices.Engine;

namespace WebUIToolkit.DependencyNotices.Security.Tests;

internal static class PathSecurityTests
{
    public static void Register(TestHarness tests)
    {
        tests.Add("paths reject traversal with both separators", RejectsTraversalAndArchiveEntries);
        tests.Add("paths reject devices and alternate data streams portably", RejectsDevicesAndAlternateStreams);
        tests.Add("paths reject symbolic link escape", RejectsSymbolicLinkEscape);
        tests.Add("paths retain containment for an ordinary nested path", AcceptsContainedPath);
    }

    private static void RejectsTraversalAndArchiveEntries() => AssertRejectedFixtureArray("archive-and-traversal-paths.json", "rejected");

    private static void RejectsDevicesAndAlternateStreams() => AssertRejectedFixtureArray("device-and-ads-paths.json", "rejected");

    private static void RejectsSymbolicLinkEscape()
    {
        string parent = Path.Combine(Path.GetTempPath(), "WebUIToolkit.DependencyNotices.Security.Tests", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(parent, "root");
        string outside = Path.Combine(parent, "outside");
        string link = Path.Combine(root, "linked");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            try
            {
                _ = Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                return;
            }

            AssertUnsafe(root, "linked/escape.txt");
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link, recursive: false);
            }

            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }

    private static void AcceptsContainedPath()
    {
        TestFiles.WithTemporaryDirectory(root =>
        {
            string actual = SafePath.ResolveContainedPath(root, "nested/evidence.txt");
            string expected = Path.GetFullPath(Path.Combine(root, "nested", "evidence.txt"));
            Assert.Equal(expected, actual);
        });
    }

    private static void AssertRejectedFixtureArray(string fixtureName, string propertyName)
    {
        using JsonDocument fixture = TestFiles.ReadFixture(fixtureName);
        TestFiles.WithTemporaryDirectory(root =>
        {
            foreach (JsonElement value in fixture.RootElement.GetProperty(propertyName).EnumerateArray())
            {
                AssertUnsafe(root, value.GetString()!);
            }
        });
    }

    private static void AssertUnsafe(string root, string path)
    {
        NoticeSecurityException exception = Assert.Throws<NoticeSecurityException>(() => SafePath.ResolveContainedPath(root, path));
        Assert.Equal("WUTNOTICE6001", exception.Code);
    }
}
