using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Acquisition.Tests;

internal static class CacheTests
{
    public static void Register(TestHarness tests)
    {
        tests.Add("cache.uses-sha256-layout", UsesSha256LayoutAsync);
        tests.Add("cache.rejects-noncanonical-digest", RejectsNoncanonicalDigest);
        tests.Add("cache.removes-temp-on-size-failure", RemovesTempOnSizeFailureAsync);
        tests.Add("cache.removes-temp-on-digest-failure", RemovesTempOnDigestFailureAsync);
        tests.Add("cache.rejects-corrupt-existing-entry", RejectsCorruptExistingAsync);
        tests.Add("cache.enforces-limit-on-existing-entry", EnforcesLimitOnExistingAsync);
        tests.Add("cache.concurrent-writers-converge", ConcurrentWritersConvergeAsync);
        tests.Add("cache.waits-for-transient-share-lock", WaitsForTransientShareLockAsync);
        tests.Add("cache.differing-writer-never-overwrites", DifferingWriterNeverOverwritesAsync);
    }

    private static async ValueTask UsesSha256LayoutAsync()
    {
        using TemporaryDirectory directory = new();
        ContentAddressedEvidenceStore store = new(directory.Path);
        byte[] bytes = "immutable bytes"u8.ToArray();
        string digest = Digest(bytes);
        CacheCommitResult result = await store.CommitAsync(new MemoryStream(bytes), digest, 1024).ConfigureAwait(false);
        Assert.Equal(System.IO.Path.Combine(directory.Path, "sha256", digest), result.Path);
        Assert.False(result.WasAlreadyCached);
        byte[] stored = await File.ReadAllBytesAsync(result.Path).ConfigureAwait(false);
        Assert.True(bytes.SequenceEqual(stored));
    }

    private static void RejectsNoncanonicalDigest()
    {
        using TemporaryDirectory directory = new();
        ContentAddressedEvidenceStore store = new(directory.Path);
        _ = Assert.Throws<ArgumentException>(() => store.GetPath(new string('A', 64)));
        _ = Assert.Throws<ArgumentException>(() => store.GetPath("../escape"));
    }

    private static async ValueTask RemovesTempOnSizeFailureAsync()
    {
        using TemporaryDirectory directory = new();
        ContentAddressedEvidenceStore store = new(directory.Path);
        byte[] bytes = "too-large"u8.ToArray();
        AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(
            () => store.CommitAsync(new MemoryStream(bytes), Digest(bytes), 2));
        Assert.Equal(NoticeDiagnosticCodes.AcquisitionSizeLimit, exception.Code);
        Assert.Equal(0, Directory.EnumerateFiles(System.IO.Path.Combine(directory.Path, "sha256")).Count());
    }

    private static async ValueTask RemovesTempOnDigestFailureAsync()
    {
        using TemporaryDirectory directory = new();
        ContentAddressedEvidenceStore store = new(directory.Path);
        AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(
            () => store.CommitAsync(new MemoryStream("actual"u8.ToArray()), Digest("expected"u8.ToArray()), 1024));
        Assert.Equal(NoticeDiagnosticCodes.AcquisitionDigestMismatch, exception.Code);
        Assert.Equal(0, Directory.EnumerateFiles(System.IO.Path.Combine(directory.Path, "sha256")).Count());
    }

    private static async ValueTask RejectsCorruptExistingAsync()
    {
        using TemporaryDirectory directory = new();
        ContentAddressedEvidenceStore store = new(directory.Path);
        byte[] expected = "expected"u8.ToArray();
        string digest = Digest(expected);
        await File.WriteAllBytesAsync(store.GetPath(digest), "corrupt"u8.ToArray()).ConfigureAwait(false);
        AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(
            () => store.CommitAsync(new MemoryStream(expected), digest, 1024));
        Assert.Equal(NoticeDiagnosticCodes.AcquisitionDigestMismatch, exception.Code);
        byte[] stored = await File.ReadAllBytesAsync(store.GetPath(digest)).ConfigureAwait(false);
        Assert.True(Encoding.UTF8.GetBytes("corrupt").SequenceEqual(stored));
    }

    private static async ValueTask EnforcesLimitOnExistingAsync()
    {
        using TemporaryDirectory directory = new();
        ContentAddressedEvidenceStore store = new(directory.Path);
        byte[] bytes = "existing"u8.ToArray();
        string digest = Digest(bytes);
        await File.WriteAllBytesAsync(store.GetPath(digest), bytes).ConfigureAwait(false);
        AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(
            () => store.CommitAsync(new MemoryStream(bytes), digest, maximumBytes: 1));
        Assert.Equal(NoticeDiagnosticCodes.AcquisitionSizeLimit, exception.Code);
    }

    private static async ValueTask ConcurrentWritersConvergeAsync()
    {
        using TemporaryDirectory directory = new();
        ContentAddressedEvidenceStore store = new(directory.Path);
        byte[] bytes = Enumerable.Range(0, 256 * 1024).Select(static value => (byte)value).ToArray();
        string digest = Digest(bytes);
        List<Task<CacheCommitResult>> tasks = [];
        for (int index = 0; index < 12; index++)
        {
            tasks.Add(store.CommitAsync(new MemoryStream(bytes), digest, bytes.Length).AsTask());
        }

        CacheCommitResult[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        Assert.True(results.All(result => result.Path == store.GetPath(digest)));
        Assert.Equal(1, results.Count(static result => !result.WasAlreadyCached));
        byte[] stored = await File.ReadAllBytesAsync(store.GetPath(digest)).ConfigureAwait(false);
        Assert.True(bytes.SequenceEqual(stored));
        Assert.Equal(1, Directory.EnumerateFiles(System.IO.Path.Combine(directory.Path, "sha256")).Count());
    }

    private static async ValueTask DifferingWriterNeverOverwritesAsync()
    {
        using TemporaryDirectory directory = new();
        ContentAddressedEvidenceStore store = new(directory.Path);
        byte[] approved = "approved"u8.ToArray();
        byte[] attacker = "different"u8.ToArray();
        string digest = Digest(approved);
        Task<CacheCommitResult> good = store.CommitAsync(new MemoryStream(approved), digest, 1024).AsTask();
        Task<AcquisitionException> bad = CaptureAcquisitionExceptionAsync(
            store.CommitAsync(new MemoryStream(attacker), digest, 1024));
        await Task.WhenAll(good, bad).ConfigureAwait(false);
        Assert.Equal(NoticeDiagnosticCodes.AcquisitionDigestMismatch, bad.Result.Code);
        byte[] stored = await File.ReadAllBytesAsync(store.GetPath(digest)).ConfigureAwait(false);
        Assert.True(approved.SequenceEqual(stored));
    }

    private static async ValueTask WaitsForTransientShareLockAsync()
    {
        using TemporaryDirectory directory = new();
        ContentAddressedEvidenceStore store = new(directory.Path);
        byte[] bytes = "existing"u8.ToArray();
        string digest = Digest(bytes);
        string path = store.GetPath(digest);
        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);

        Task<CacheCommitResult> commit;
        using (FileStream exclusive = new(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            commit = store.CommitAsync(new MemoryStream(bytes), digest, 1024).AsTask();
            await Task.Delay(50).ConfigureAwait(false);
            Assert.False(commit.IsCompleted);
        }

        CacheCommitResult result = await commit.ConfigureAwait(false);
        Assert.True(result.WasAlreadyCached);
        Assert.Equal(path, result.Path);
    }

    private static async Task<AcquisitionException> CaptureAcquisitionExceptionAsync<T>(ValueTask<T> operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (AcquisitionException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected AcquisitionException.");
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
