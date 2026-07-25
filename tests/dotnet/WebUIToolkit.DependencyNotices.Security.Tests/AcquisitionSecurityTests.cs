using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.DependencyNotices.Acquisition;
using WebUIToolkit.DependencyNotices.Engine;

namespace WebUIToolkit.DependencyNotices.Security.Tests;

internal static class AcquisitionSecurityTests
{
    private static readonly string[] AllowedHosts = ["example.invalid"];

    public static void Register(TestHarness tests)
    {
        tests.Add("offline engine operations cannot opt into network", EngineOfflineOperationsStayOffline);
        tests.Add("offline acquisition operations do not touch transport", OfflineAcquisitionDoesNotTouchTransport);
        tests.Add("acquisition diagnostics redact credentials and query", RedactsCredentialBearingOrigin);
        tests.Add("cache collision never overwrites differing bytes", CacheCollisionDoesNotOverwrite);
        tests.Add("concurrent cache writers converge atomically", ConcurrentCacheWritersConverge);
        tests.Add("cache rejects a linked ancestor", CacheRejectsLinkedAncestor);
        tests.Add("concurrent origin-index writers leave one complete document", ConcurrentIndexWritersRemainAtomic);
        tests.Add("origin index rejects one origin mapped to different digests", OriginIndexRejectsDigestCollision);
    }

    private static void EngineOfflineOperationsStayOffline()
    {
        NoticeOperation[] operations =
        [
            NoticeOperation.Scan,
            NoticeOperation.Evaluate,
            NoticeOperation.Generate,
            NoticeOperation.Verify,
        ];
        foreach (NoticeOperation operation in operations)
        {
            foreach (bool allowNetwork in new[] { false, true })
            {
                NoticeSecurityException exception = Assert.Throws<NoticeSecurityException>(() => NetworkPolicy.EnsurePermitted(operation, allowNetwork));
                Assert.Equal("WUTNOTICE7001", exception.Code);
            }
        }
    }

    private static async ValueTask OfflineAcquisitionDoesNotTouchTransport()
    {
        await TestFiles.WithTemporaryDirectoryAsync(async root =>
        {
            CountingHandler handler = new();
            ContentAddressedEvidenceStore store = new(root);
            AcquisitionPolicy policy = new(AllowedHosts);
            using EvidenceAcquirer acquirer = new(handler, store, policy, disposeHandler: false);
            foreach (AcquisitionOperation operation in new[]
            {
                AcquisitionOperation.Scan,
                AcquisitionOperation.Evaluate,
                AcquisitionOperation.Generate,
                AcquisitionOperation.Verify,
            })
            {
                AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(async () =>
                {
                    _ = await acquirer.AcquireAsync(new AcquisitionRequest(
                        operation,
                        true,
                        new Uri("https://example.invalid/evidence"),
                        new string('0', 64))).ConfigureAwait(false);
                }).ConfigureAwait(false);
                Assert.Equal("WUTNOTICE7001", exception.Code);
            }

            Assert.Equal(0, handler.RequestCount);
        }).ConfigureAwait(false);
    }

    private static async ValueTask RedactsCredentialBearingOrigin()
    {
        using JsonDocumentFixture fixture = new("credential-redaction.json");
        string input = fixture.String("input");
        string[] forbidden = fixture.StringArray("forbidden");
        await TestFiles.WithTemporaryDirectoryAsync(async root =>
        {
            CountingHandler handler = new();
            ContentAddressedEvidenceStore store = new(root);
            AcquisitionPolicy policy = new(AllowedHosts);
            using EvidenceAcquirer acquirer = new(handler, store, policy, disposeHandler: false);
            AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(async () =>
            {
                _ = await acquirer.AcquireAsync(new AcquisitionRequest(
                    AcquisitionOperation.Acquire,
                    true,
                    new Uri(input),
                    new string('0', 64))).ConfigureAwait(false);
            }).ConfigureAwait(false);

            foreach (string secret in forbidden)
            {
                Assert.DoesNotContain(secret, exception.Message, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Equal(0, handler.RequestCount);
        }).ConfigureAwait(false);
    }

    private static async ValueTask CacheCollisionDoesNotOverwrite()
    {
        await TestFiles.WithTemporaryDirectoryAsync(async root =>
        {
            byte[] expected = Encoding.UTF8.GetBytes("expected bytes");
            byte[] hostile = Encoding.UTF8.GetBytes("hostile collision");
            string digest = Convert.ToHexStringLower(SHA256.HashData(expected));
            ContentAddressedEvidenceStore store = new(root);
            string destination = store.GetPath(digest);
            File.WriteAllBytes(destination, hostile);
            AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(async () =>
            {
                await using MemoryStream stream = new(expected, writable: false);
                _ = await store.CommitAsync(stream, digest, 1024).ConfigureAwait(false);
            }).ConfigureAwait(false);

            Assert.Equal("WUTNOTICE7005", exception.Code);
            Assert.True(File.ReadAllBytes(destination).SequenceEqual(hostile), "A collision must not overwrite existing bytes.");
        }).ConfigureAwait(false);
    }

    private static async ValueTask ConcurrentCacheWritersConverge()
    {
        await TestFiles.WithTemporaryDirectoryAsync(async root =>
        {
            byte[] bytes = Encoding.UTF8.GetBytes(new string('x', 65_537));
            string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            ContentAddressedEvidenceStore[] stores = Enumerable.Range(0, 16)
                .Select(_ => new ContentAddressedEvidenceStore(root))
                .ToArray();
            Task<CacheCommitResult>[] writers = stores.Select(store => Task.Run(async () =>
            {
                await using MemoryStream stream = new(bytes, writable: false);
                return await store.CommitAsync(stream, digest, 1_000_000).ConfigureAwait(false);
            })).ToArray();

            CacheCommitResult[] results = await Task.WhenAll(writers).ConfigureAwait(false);
            Assert.True(results.All(result => result.Path == stores[0].GetPath(digest)));
            Assert.Equal(1, results.Count(result => !result.WasAlreadyCached));
            Assert.True(File.ReadAllBytes(stores[0].GetPath(digest)).SequenceEqual(bytes));
            Assert.False(Directory.EnumerateFiles(Path.Combine(root, "sha256"), ".tmp-*").Any(), "Temporary cache files must be cleaned up.");
        }).ConfigureAwait(false);
    }

    private static void CacheRejectsLinkedAncestor()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        TestFiles.WithTemporaryDirectory(root =>
        {
            string actual = Path.Combine(root, "actual");
            string link = Path.Combine(root, "linked");
            Directory.CreateDirectory(actual);
            Directory.CreateSymbolicLink(link, actual);
            AcquisitionException exception = Assert.Throws<AcquisitionException>(
                () => _ = new ContentAddressedEvidenceStore(Path.Combine(link, "cache")));
            Assert.Equal("WUTNOTICE7002", exception.Code);
        });
    }

    private static async ValueTask ConcurrentIndexWritersRemainAtomic()
    {
        await TestFiles.WithTemporaryDirectoryAsync(async root =>
        {
            string path = Path.Combine(root, "origin-index.json");
            EvidenceOriginIndexEntry[] first = [new(new Uri("https://one.example/evidence"), new string('1', 64))];
            EvidenceOriginIndexEntry[] second = [new(new Uri("https://two.example/evidence"), new string('2', 64))];
            byte[] firstBytes = EvidenceOriginIndex.Serialize(first);
            byte[] secondBytes = EvidenceOriginIndex.Serialize(second);
            Task[] writers = Enumerable.Range(0, 20).Select(index => Task.Run(async () =>
            {
                await EvidenceOriginIndex.WriteAsync(path, index % 2 == 0 ? first : second).ConfigureAwait(false);
            })).ToArray();

            await Task.WhenAll(writers).ConfigureAwait(false);
            byte[] actual = File.ReadAllBytes(path);
            Assert.True(actual.SequenceEqual(firstBytes) || actual.SequenceEqual(secondBytes), "Final index must be one complete canonical document.");
            Assert.False(Directory.EnumerateFiles(root, ".origin-index-*").Any(), "Temporary index files must be cleaned up.");
        }).ConfigureAwait(false);
    }

    private static void OriginIndexRejectsDigestCollision()
    {
        Uri origin = new("https://example.invalid/evidence");
        Assert.Throws<ArgumentException>(() => EvidenceOriginIndex.Serialize(new[]
        {
            new EvidenceOriginIndexEntry(origin, new string('1', 64)),
            new EvidenceOriginIndexEntry(origin, new string('2', 64)),
        }));
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            });
        }
    }
}
