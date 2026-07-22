using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WebUIToolkit.DependencyNotices.Acquisition.Tests;

internal static class OriginIndexTests
{
    private static readonly string DigestA = new('a', 64);
    private static readonly string DigestB = new('b', 64);

    public static void Register(TestHarness tests)
    {
        tests.Add("origin-index.matches-canonical-fixture", MatchesCanonicalFixture);
        tests.Add("origin-index.is-order-and-culture-independent", IsOrderAndCultureIndependent);
        tests.Add("origin-index.collapses-identical-duplicates", CollapsesIdenticalDuplicates);
        tests.Add("origin-index.rejects-conflicting-digests", RejectsConflictingDigests);
        tests.Add("origin-index.rejects-credentials", RejectsCredentials);
        tests.Add("origin-index.strips-sensitive-query-and-fragment", StripsSensitiveQueryAndFragment);
        tests.Add("origin-index.write-is-complete-and-temp-free", WritesCompleteFileAsync);
        tests.Add("origin-index.concurrent-writers-converge", ConcurrentWritersConvergeAsync);
    }

    private static void MatchesCanonicalFixture()
    {
        byte[] actual = EvidenceOriginIndex.Serialize(CanonicalEntries());
        byte[] expected = File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "origin-index.expected.json"));
        Assert.True(expected.AsSpan().SequenceEqual(actual), Encoding.UTF8.GetString(actual));
        Assert.False(actual.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal((byte)'\n', actual[^1]);
        Assert.False(actual.AsSpan().Contains((byte)'\r'));
    }

    private static void IsOrderAndCultureIndependent()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            byte[] forward = EvidenceOriginIndex.Serialize(CanonicalEntries());
            byte[] reverse = EvidenceOriginIndex.Serialize(CanonicalEntries().Reverse());
            Assert.True(forward.AsSpan().SequenceEqual(reverse));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static void CollapsesIdenticalDuplicates()
    {
        EvidenceOriginIndexEntry entry = new(new Uri("https://a.example/license"), DigestA);
        string json = Encoding.UTF8.GetString(EvidenceOriginIndex.Serialize([entry, entry]));
        Assert.Equal(1, Count(json, "\"origin\""));
    }

    private static void RejectsConflictingDigests()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => EvidenceOriginIndex.Serialize(
        [
            new EvidenceOriginIndexEntry(new Uri("https://a.example/license?token=secret"), DigestA),
            new EvidenceOriginIndexEntry(new Uri("https://a.example/license?token=secret"), DigestB),
        ]));
        Assert.False(exception.Message.Contains("token", StringComparison.Ordinal));
        Assert.False(exception.Message.Contains("secret", StringComparison.Ordinal));
    }

    private static void RejectsCredentials()
    {
        _ = Assert.Throws<ArgumentException>(() => EvidenceOriginIndex.Serialize(
        [
            new EvidenceOriginIndexEntry(new Uri("https://user:password@a.example/license"), DigestA),
        ]));
    }

    private static void StripsSensitiveQueryAndFragment()
    {
        string json = Encoding.UTF8.GetString(EvidenceOriginIndex.Serialize(
        [
            new EvidenceOriginIndexEntry(new Uri("https://a.example/license?token=fixture-secret#section"), DigestA),
        ]));
        Assert.False(json.Contains("token", StringComparison.Ordinal));
        Assert.False(json.Contains("fixture-secret", StringComparison.Ordinal));
        Assert.False(json.Contains("#section", StringComparison.Ordinal));
    }

    private static async ValueTask WritesCompleteFileAsync()
    {
        using TemporaryDirectory directory = new();
        string path = System.IO.Path.Combine(directory.Path, "origin-index.v1.json");
        await EvidenceOriginIndex.WriteAsync(path, CanonicalEntries()).ConfigureAwait(false);
        byte[] expected = EvidenceOriginIndex.Serialize(CanonicalEntries());
        byte[] actual = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        Assert.True(expected.SequenceEqual(actual));
        Assert.Equal(1, Directory.EnumerateFiles(directory.Path).Count());
    }

    private static async ValueTask ConcurrentWritersConvergeAsync()
    {
        using TemporaryDirectory directory = new();
        string path = System.IO.Path.Combine(directory.Path, "origin-index.v1.json");
        Task[] writers = Enumerable.Range(0, 24)
            .Select(_ => EvidenceOriginIndex.WriteAsync(path, CanonicalEntries()).AsTask())
            .ToArray();
        await Task.WhenAll(writers).ConfigureAwait(false);
        byte[] expected = EvidenceOriginIndex.Serialize(CanonicalEntries());
        byte[] actual = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        Assert.True(expected.SequenceEqual(actual));
        Assert.Equal(1, Directory.EnumerateFiles(directory.Path).Count());
    }

    private static EvidenceOriginIndexEntry[] CanonicalEntries() =>
    [
        new(new Uri("https://z.example/license?token=fixture-secret"), DigestB),
        new(new Uri("https://a.example/license"), DigestA),
    ];

    private static int Count(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
