using System;
using System.IO;
using WebUIToolkit.DependencyNotices.Engine;

namespace WebUIToolkit.DependencyNotices.Tests;

internal static class SecurityTests
{
    private const string UnsafePathCode = "WUTNOTICE6001";
    private const string NetworkForbiddenCode = "WUTNOTICE7001";

    public static void Register(TestHarness tests)
    {
        tests.Add("security rejects parent traversal", RejectsParentTraversal);
        tests.Add("security rejects absolute paths", RejectsAbsolutePaths);
        tests.Add("security rejects Windows device paths", RejectsWindowsDevicePaths);
        tests.Add("security rejects alternate data stream paths", RejectsAlternateDataStreamPaths);
        tests.Add("security rejects containment prefix traps", RejectsContainmentPrefixTraps);
        tests.Add("security accepts output contained by root", AcceptsContainedOutput);
        tests.Add("security rejects symlink or reparse-point escape when supported", RejectsLinkEscapeWhenSupported);
        tests.Add("offline policy denies non-acquisition operations regardless of flag", DeniesNetworkForOfflineOperations);
        tests.Add("offline policy requires explicit acquisition opt-in", RequiresExplicitAcquisitionOptIn);
    }

    private static void RejectsParentTraversal()
    {
        WithTemporaryDirectory(root =>
        {
            AssertUnsafePath(root, "../escape.txt");
            AssertUnsafePath(root, "nested/../../escape.txt");
            AssertUnsafePath(root, @"nested\..\..\escape.txt");
        });
    }

    private static void RejectsAbsolutePaths()
    {
        WithTemporaryDirectory(root =>
        {
            string absolutePath = Path.Combine(Path.GetPathRoot(root)!, "dependency-notices-escape.txt");
            AssertUnsafePath(root, absolutePath);
            AssertUnsafePath(root, "/dependency-notices-escape.txt");
        });
    }

    private static void RejectsWindowsDevicePaths()
    {
        WithTemporaryDirectory(root =>
        {
            AssertUnsafePath(root, @"\\.\C:\dependency-notices-escape.txt");
            AssertUnsafePath(root, @"\\?\C:\dependency-notices-escape.txt");
            AssertUnsafePath(root, @"\\server\share\dependency-notices-escape.txt");
        });
    }

    private static void RejectsAlternateDataStreamPaths()
    {
        WithTemporaryDirectory(root =>
        {
            AssertUnsafePath(root, "notice.txt:secret");
            AssertUnsafePath(root, "nested/notice.txt:$DATA");
        });
    }

    private static void RejectsContainmentPrefixTraps()
    {
        string parent = CreateTemporaryDirectory();
        try
        {
            string root = Path.Combine(parent, "evidence");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(parent, "evidence-attacker"));

            AssertUnsafePath(root, "../evidence-attacker/output.txt");
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static void AcceptsContainedOutput()
    {
        WithTemporaryDirectory(root =>
        {
            string expected = Path.GetFullPath(Path.Combine(root, "nested", "notices.json"));
            string actual = SafePath.ResolveContainedPath(root, "nested/notices.json");

            Assert.Equal(expected, actual);
            Assert.True(IsContainedBy(root, actual), "Resolved output must remain below the declared root.");
        });
    }

    private static void RejectsLinkEscapeWhenSupported()
    {
        string parent = CreateTemporaryDirectory();
        string root = Path.Combine(parent, "root");
        string outside = Path.Combine(parent, "outside");
        string link = Path.Combine(root, "linked");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);

        try
        {
            if (!TryCreateDirectoryLink(link, outside))
            {
                return;
            }

            AssertUnsafePath(root, "linked/escape.txt");
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link, recursive: false);
            }

            Directory.Delete(parent, recursive: true);
        }
    }

    private static void DeniesNetworkForOfflineOperations()
    {
        NoticeOperation[] offlineOperations =
        [
            NoticeOperation.Scan,
            NoticeOperation.Evaluate,
            NoticeOperation.Generate,
            NoticeOperation.Verify,
        ];

        foreach (NoticeOperation operation in offlineOperations)
        {
            AssertNetworkForbidden(operation, allowNetwork: false);
            AssertNetworkForbidden(operation, allowNetwork: true);
        }
    }

    private static void RequiresExplicitAcquisitionOptIn()
    {
        AssertNetworkForbidden(NoticeOperation.Acquire, allowNetwork: false);
        NetworkPolicy.EnsurePermitted(NoticeOperation.Acquire, allowNetwork: true);
    }

    private static void AssertUnsafePath(string root, string relativePath)
    {
        NoticeSecurityException exception = Assert.Throws<NoticeSecurityException>(
            () => SafePath.ResolveContainedPath(root, relativePath));
        Assert.Equal(UnsafePathCode, exception.Code);
    }

    private static void AssertNetworkForbidden(NoticeOperation operation, bool allowNetwork)
    {
        NoticeSecurityException exception = Assert.Throws<NoticeSecurityException>(
            () => NetworkPolicy.EnsurePermitted(operation, allowNetwork));
        Assert.Equal(NetworkForbiddenCode, exception.Code);
    }

    private static bool IsContainedBy(string root, string candidate)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            _ = Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void WithTemporaryDirectory(Action<string> action)
    {
        string path = CreateTemporaryDirectory();
        try
        {
            action(path);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "WebUIToolkit.DependencyNotices.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
