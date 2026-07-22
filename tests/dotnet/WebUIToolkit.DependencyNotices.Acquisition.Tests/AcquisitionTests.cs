using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Acquisition.Tests;

internal static class AcquisitionTests
{
    private static readonly Uri AllowedOrigin = new("https://evidence.example/licenses/a.txt");

    public static void Register(TestHarness tests)
    {
        tests.Add("acquire.requires-explicit-operation", RequiresExplicitOperationAsync);
        tests.Add("acquire.requires-allow-network", RequiresAllowNetworkAsync);
        tests.Add("acquire.rejects-http-by-default", RejectsHttpByDefaultAsync);
        tests.Add("acquire.rejects-auto-redirect-handler", RejectsAutomaticRedirectHandler);
        tests.Add("acquire.permits-explicit-http", PermitsExplicitHttpAsync);
        tests.Add("acquire.enforces-exact-host-allowlist", EnforcesExactHostAsync);
        tests.Add("acquire.rejects-userinfo-and-sanitizes", RejectsUserInfoAndSanitizesAsync);
        tests.Add("acquire.follows-revalidated-relative-redirect", FollowsRelativeRedirectAsync);
        tests.Add("acquire.blocks-redirect-host", BlocksRedirectHostAsync);
        tests.Add("acquire.blocks-redirect-credentials", BlocksRedirectCredentialsAsync);
        tests.Add("acquire.rejects-redirect-without-location", RejectsMissingLocationAsync);
        tests.Add("acquire.enforces-redirect-limit", EnforcesRedirectLimitAsync);
        tests.Add("acquire.enforces-content-length-limit", EnforcesContentLengthLimitAsync);
        tests.Add("acquire.enforces-streaming-byte-limit", EnforcesStreamingLimitAsync);
        tests.Add("acquire.enforces-total-time-limit", EnforcesTimeLimitAsync);
        tests.Add("acquire.sanitizes-http-failure", SanitizesHttpFailureAsync);
        tests.Add("acquire.verifies-digest-before-cache", VerifiesDigestAsync);
        tests.Add("acquire.preserves-exact-evidence-bytes", PreservesExactBytesAsync);
        tests.Add("acquire.cache-hit-does-not-contact-handler", CacheHitAvoidsTransportAsync);
    }

    private static async ValueTask RequiresExplicitOperationAsync()
    {
        foreach (AcquisitionOperation operation in Enum.GetValues<AcquisitionOperation>().Where(static value => value != AcquisitionOperation.Acquire))
        {
            await using TestContext context = new();
            AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(
                () => context.Acquirer.AcquireAsync(new AcquisitionRequest(operation, true, AllowedOrigin, Digest("x"))));
            Assert.Equal(NoticeDiagnosticCodes.NetworkAccessForbidden, exception.Code);
            Assert.Equal(0, context.Handler.Requests.Count);
        }
    }

    private static async ValueTask RequiresAllowNetworkAsync()
    {
        await using TestContext context = new();
        AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(
            () => context.Acquirer.AcquireAsync(Request(Digest("x"), allowNetwork: false)));
        Assert.Equal(NoticeDiagnosticCodes.NetworkAccessForbidden, exception.Code);
        Assert.Equal(0, context.Handler.Requests.Count);
    }

    private static async ValueTask RejectsHttpByDefaultAsync()
    {
        await using TestContext context = new();
        AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(
            () => context.Acquirer.AcquireAsync(Request(Digest("x"), new Uri("http://evidence.example/license"))));
        Assert.Equal(NoticeDiagnosticCodes.AcquisitionOriginBlocked, exception.Code);
        Assert.Equal(0, context.Handler.Requests.Count);
    }

    private static void RejectsAutomaticRedirectHandler()
    {
        using TemporaryDirectory directory = new();
        using HttpClientHandler handler = new() { AllowAutoRedirect = true };
        ContentAddressedEvidenceStore store = new(directory.Path);
        _ = Assert.Throws<ArgumentException>(() =>
        {
            using EvidenceAcquirer unused = new(handler, store, new AcquisitionPolicy(["evidence.example"]), disposeHandler: false);
        });
    }

    private static async ValueTask PermitsExplicitHttpAsync()
    {
        byte[] bytes = "http fixture"u8.ToArray();
        await using TestContext context = new(
            response: static (_, _) => ValueTask.FromResult(Ok("http fixture"u8.ToArray())),
            policy: new AcquisitionPolicy(["evidence.example"], allowHttp: true));
        AcquisitionResult result = await context.Acquirer.AcquireAsync(
            Request(Digest(bytes), new Uri("http://evidence.example/license")));
        Assert.Equal(bytes.Length, result.ByteCount);
    }

    private static async ValueTask EnforcesExactHostAsync()
    {
        await using TestContext context = new();
        AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(
            () => context.Acquirer.AcquireAsync(Request(Digest("x"), new Uri("https://sub.evidence.example/license"))));
        Assert.Equal(NoticeDiagnosticCodes.AcquisitionOriginBlocked, exception.Code);
    }

    private static async ValueTask RejectsUserInfoAndSanitizesAsync()
    {
        await using TestContext context = new();
        AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(
            () => context.Acquirer.AcquireAsync(Request(
                Digest("x"),
                new Uri("https://alice:secret@evidence.example/license?token=sensitive#fragment"))));
        Assert.Equal(NoticeDiagnosticCodes.AcquisitionOriginBlocked, exception.Code);
        Assert.False(exception.Message.Contains("secret", StringComparison.Ordinal));
        Assert.False(exception.Message.Contains("token", StringComparison.Ordinal));
        Assert.False(exception.Message.Contains("sensitive", StringComparison.Ordinal));
        Assert.False(exception.Message.Contains("alice", StringComparison.Ordinal));
    }

    private static async ValueTask FollowsRelativeRedirectAsync()
    {
        byte[] bytes = "redirected"u8.ToArray();
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            if (request.RequestUri == AllowedOrigin)
            {
                return ValueTask.FromResult(Redirect(HttpStatusCode.Found, new Uri("../final/license.txt", UriKind.Relative)));
            }

            return ValueTask.FromResult(Ok(bytes));
        });
        await using TestContext context = new(handler: handler);
        AcquisitionResult result = await context.Acquirer.AcquireAsync(Request(Digest(bytes)));
        Assert.Equal(1, result.RedirectCount);
        Assert.Equal(new Uri("https://evidence.example/final/license.txt"), result.EffectiveOrigin);
        Assert.Equal(2, handler.Requests.Count);
    }

    private static async ValueTask BlocksRedirectHostAsync()
    {
        await using TestContext context = new(
            response: static (_, _) => ValueTask.FromResult(
                Redirect(HttpStatusCode.Found, new Uri("https://blocked.example/license?secret=value"))));
        AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(
            () => context.Acquirer.AcquireAsync(Request(Digest("x"))));
        Assert.Equal(NoticeDiagnosticCodes.AcquisitionRedirectBlocked, exception.Code);
        Assert.False(exception.Message.Contains("secret", StringComparison.Ordinal));
        Assert.Equal(1, context.Handler.Requests.Count);
    }

    private static async ValueTask BlocksRedirectCredentialsAsync()
    {
        await using TestContext context = new(
            response: static (_, _) => ValueTask.FromResult(
                Redirect(HttpStatusCode.TemporaryRedirect, new Uri("https://alice:password@evidence.example/license"))));
        AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(
            () => context.Acquirer.AcquireAsync(Request(Digest("x"))));
        Assert.Equal(NoticeDiagnosticCodes.AcquisitionRedirectBlocked, exception.Code);
        Assert.False(exception.Message.Contains("password", StringComparison.Ordinal));
        Assert.False(exception.Message.Contains("alice", StringComparison.Ordinal));
    }

    private static async ValueTask RejectsMissingLocationAsync()
    {
        await using TestContext context = new(
            response: static (_, _) => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.Found)));
        AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(
            () => context.Acquirer.AcquireAsync(Request(Digest("x"))));
        Assert.Equal(NoticeDiagnosticCodes.AcquisitionRedirectBlocked, exception.Code);
    }

    private static async ValueTask EnforcesRedirectLimitAsync()
    {
        await using TestContext context = new(
            response: static (_, _) => ValueTask.FromResult(Redirect(HttpStatusCode.Found, new Uri("/again", UriKind.Relative))),
            policy: new AcquisitionPolicy(["evidence.example"], maximumRedirects: 2));
        AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(
            () => context.Acquirer.AcquireAsync(Request(Digest("x"))));
        Assert.Equal(NoticeDiagnosticCodes.AcquisitionRedirectBlocked, exception.Code);
        Assert.Equal(3, context.Handler.Requests.Count);
    }

    private static async ValueTask EnforcesContentLengthLimitAsync()
    {
        HttpResponseMessage response = Ok("12345"u8.ToArray());
        response.Content.Headers.ContentLength = 5;
        await using TestContext context = new(
            response: (_, _) => ValueTask.FromResult(response),
            policy: new AcquisitionPolicy(["evidence.example"], maximumBytes: 4));
        AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(
            () => context.Acquirer.AcquireAsync(Request(Digest("12345"))));
        Assert.Equal(NoticeDiagnosticCodes.AcquisitionSizeLimit, exception.Code);
    }

    private static async ValueTask EnforcesStreamingLimitAsync()
    {
        HttpResponseMessage response = Ok("12345"u8.ToArray());
        response.Content.Headers.ContentLength = null;
        await using TestContext context = new(
            response: (_, _) => ValueTask.FromResult(response),
            policy: new AcquisitionPolicy(["evidence.example"], maximumBytes: 4));
        AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(
            () => context.Acquirer.AcquireAsync(Request(Digest("12345"))));
        Assert.Equal(NoticeDiagnosticCodes.AcquisitionSizeLimit, exception.Code);
        Assert.Equal(0, Directory.EnumerateFiles(System.IO.Path.Combine(context.Directory.Path, "sha256")).Count());
    }

    private static async ValueTask EnforcesTimeLimitAsync()
    {
        await using TestContext context = new(
            response: static async (_, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                return Ok("late"u8.ToArray());
            },
            policy: new AcquisitionPolicy(["evidence.example"], timeout: TimeSpan.FromMilliseconds(20)));
        AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(
            () => context.Acquirer.AcquireAsync(Request(Digest("late"))));
        Assert.Equal(NoticeDiagnosticCodes.AcquisitionOriginBlocked, exception.Code);
        Assert.True(exception.Message.Contains("time limit", StringComparison.Ordinal));
    }

    private static async ValueTask SanitizesHttpFailureAsync()
    {
        await using TestContext context = new(
            response: static (_, _) => ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(
            () => context.Acquirer.AcquireAsync(Request(
                Digest("x"),
                new Uri("https://evidence.example/license?token=sensitive"))));
        Assert.Equal(NoticeDiagnosticCodes.AcquisitionOriginBlocked, exception.Code);
        Assert.False(exception.Message.Contains("token", StringComparison.Ordinal));
        Assert.False(exception.Message.Contains("sensitive", StringComparison.Ordinal));
    }

    private static async ValueTask VerifiesDigestAsync()
    {
        await using TestContext context = new(response: static (_, _) => ValueTask.FromResult(Ok("actual"u8.ToArray())));
        AcquisitionException exception = await Assert.ThrowsAsync<AcquisitionException>(
            () => context.Acquirer.AcquireAsync(Request(Digest("expected"))));
        Assert.Equal(NoticeDiagnosticCodes.AcquisitionDigestMismatch, exception.Code);
        Assert.False(context.Store.Contains(Digest("expected")));
    }

    private static async ValueTask PreservesExactBytesAsync()
    {
        byte[] bytes = [0xef, 0xbb, 0xbf, 0x41, 0x0d, 0x0a, 0x00, 0xff];
        await using TestContext context = new(response: (_, _) => ValueTask.FromResult(Ok(bytes)));
        AcquisitionResult result = await context.Acquirer.AcquireAsync(Request(Digest(bytes)));
        byte[] stored = await File.ReadAllBytesAsync(result.CachePath).ConfigureAwait(false);
        Assert.True(bytes.SequenceEqual(stored));
        Assert.Equal(Digest(bytes), System.IO.Path.GetFileName(result.CachePath));
    }

    private static async ValueTask CacheHitAvoidsTransportAsync()
    {
        byte[] bytes = "cached"u8.ToArray();
        await using TestContext context = new(response: static (_, _) => throw new InvalidOperationException("Transport must not be invoked."));
        await context.Store.CommitAsync(new MemoryStream(bytes), Digest(bytes), bytes.Length).ConfigureAwait(false);
        AcquisitionResult result = await context.Acquirer.AcquireAsync(Request(Digest(bytes)));
        Assert.True(result.WasAlreadyCached);
        Assert.Equal(0, context.Handler.Requests.Count);
    }

    private static AcquisitionRequest Request(string digest, Uri? origin = null, bool allowNetwork = true) =>
        new(AcquisitionOperation.Acquire, allowNetwork, origin ?? AllowedOrigin, digest);

    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
    };

    private static HttpResponseMessage Redirect(HttpStatusCode status, Uri location)
    {
        HttpResponseMessage response = new(status);
        response.Headers.Location = location;
        return response;
    }

    private static string Digest(string text) => Digest(Encoding.UTF8.GetBytes(text));

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(
            Func<HttpRequestMessage, System.Threading.CancellationToken, ValueTask<HttpResponseMessage>>? response = null,
            FakeHttpMessageHandler? handler = null,
            AcquisitionPolicy? policy = null)
        {
            Directory = new TemporaryDirectory();
            Store = new ContentAddressedEvidenceStore(Directory.Path);
            Handler = handler ?? new FakeHttpMessageHandler(response ?? (static (_, _) => ValueTask.FromResult(Ok("x"u8.ToArray()))));
            Acquirer = new EvidenceAcquirer(
                Handler,
                Store,
                policy ?? new AcquisitionPolicy(["evidence.example"]),
                disposeHandler: false);
        }

        public TemporaryDirectory Directory { get; }

        public ContentAddressedEvidenceStore Store { get; }

        public FakeHttpMessageHandler Handler { get; }

        public EvidenceAcquirer Acquirer { get; }

        public ValueTask DisposeAsync()
        {
            Acquirer.Dispose();
            Handler.Dispose();
            Directory.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
