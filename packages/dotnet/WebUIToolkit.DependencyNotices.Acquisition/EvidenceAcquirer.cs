using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Evidence;

namespace WebUIToolkit.DependencyNotices.Acquisition;

public sealed class EvidenceAcquirer : IDisposable
{
    private readonly HttpMessageInvoker _transport;
    private readonly ContentAddressedEvidenceStore _store;
    private readonly AcquisitionPolicy _policy;
    private bool _disposed;

    public EvidenceAcquirer(
        HttpMessageHandler handler,
        ContentAddressedEvidenceStore store,
        AcquisitionPolicy policy,
        bool disposeHandler = true)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureManualRedirectHandling(handler);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _transport = new HttpMessageInvoker(handler, disposeHandler);
    }

    public static EvidenceAcquirer CreateDefault(
        ContentAddressedEvidenceStore store,
        AcquisitionPolicy policy)
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
        };
        return new EvidenceAcquirer(handler, store, policy);
    }

    public async ValueTask<AcquisitionResult> AcquireAsync(
        AcquisitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        OriginPolicy.EnsureAuthorized(request);
        if (!EvidenceDigest.IsCanonicalSha256(request.ExpectedSha256))
        {
            throw new ArgumentException("A lowercase canonical SHA-256 digest is required.", nameof(request));
        }

        OriginPolicy.EnsureAllowed(request.Origin, _policy, isRedirect: false);

        string cachePath = _store.GetPath(request.ExpectedSha256);
        if (_store.Contains(request.ExpectedSha256))
        {
            CacheCommitResult existing = await _store.CommitAsync(
                Stream.Null,
                request.ExpectedSha256,
                _policy.MaximumBytes,
                cancellationToken).ConfigureAwait(false);
            return new AcquisitionResult(
                OriginPolicy.SanitizeUri(request.Origin),
                OriginPolicy.SanitizeUri(request.Origin),
                request.ExpectedSha256,
                existing.Path,
                existing.ByteCount,
                0,
                true);
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_policy.Timeout);
        Uri current = request.Origin;
        int redirects = 0;

        try
        {
            while (true)
            {
                using HttpRequestMessage message = new(HttpMethod.Get, current);
                using HttpResponseMessage response = await _transport.SendAsync(
                    message,
                    timeout.Token).ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirects >= _policy.MaximumRedirects)
                    {
                        throw new AcquisitionException(
                            NoticeDiagnosticCodes.AcquisitionRedirectBlocked,
                            $"Acquisition exceeded the configured {_policy.MaximumRedirects} redirect limit at '{OriginPolicy.Sanitize(current)}'.");
                    }

                    Uri next = ResolveRedirect(current, response.Headers.Location);
                    OriginPolicy.EnsureAllowed(next, _policy, isRedirect: true);
                    current = next;
                    redirects++;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new AcquisitionException(
                        NoticeDiagnosticCodes.AcquisitionOriginBlocked,
                        $"Acquisition from '{OriginPolicy.Sanitize(current)}' failed with HTTP status {(int)response.StatusCode}.");
                }

                long? declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength > _policy.MaximumBytes)
                {
                    throw new AcquisitionException(
                        NoticeDiagnosticCodes.AcquisitionSizeLimit,
                        $"Acquired evidence declared {declaredLength.Value} bytes, exceeding the configured {_policy.MaximumBytes} byte limit.");
                }

                await using Stream content = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                CacheCommitResult commit = await _store.CommitAsync(
                    content,
                    request.ExpectedSha256,
                    _policy.MaximumBytes,
                    timeout.Token).ConfigureAwait(false);
                return new AcquisitionResult(
                    OriginPolicy.SanitizeUri(request.Origin),
                    OriginPolicy.SanitizeUri(current),
                    request.ExpectedSha256,
                    commit.Path,
                    commit.ByteCount,
                    redirects,
                    commit.WasAlreadyCached);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AcquisitionException(
                NoticeDiagnosticCodes.AcquisitionOriginBlocked,
                $"Acquisition from '{OriginPolicy.Sanitize(current)}' exceeded the configured time limit.");
        }
        catch (HttpRequestException)
        {
            throw new AcquisitionException(
                NoticeDiagnosticCodes.AcquisitionOriginBlocked,
                $"Acquisition from '{OriginPolicy.Sanitize(current)}' failed at the network boundary.");
        }
        catch (IOException)
        {
            throw new AcquisitionException(
                NoticeDiagnosticCodes.AcquisitionOriginBlocked,
                $"Acquisition from '{OriginPolicy.Sanitize(current)}' failed while reading evidence bytes.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _transport.Dispose();
        _disposed = true;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Found or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static Uri ResolveRedirect(Uri current, Uri? location)
    {
        if (location is null)
        {
            throw new AcquisitionException(
                NoticeDiagnosticCodes.AcquisitionRedirectBlocked,
                $"A redirect from '{OriginPolicy.Sanitize(current)}' did not include a Location header.");
        }

        return location.IsAbsoluteUri ? location : new Uri(current, location);
    }

    private static void EnsureManualRedirectHandling(HttpMessageHandler handler)
    {
        HttpMessageHandler current = handler;
        while (true)
        {
            if (current is HttpClientHandler clientHandler && clientHandler.AllowAutoRedirect)
            {
                throw new ArgumentException(
                    "Automatic redirects must be disabled so every redirect can be revalidated.",
                    nameof(handler));
            }

            if (current is SocketsHttpHandler socketsHandler && socketsHandler.AllowAutoRedirect)
            {
                throw new ArgumentException(
                    "Automatic redirects must be disabled so every redirect can be revalidated.",
                    nameof(handler));
            }

            if (current is not DelegatingHandler delegatingHandler)
            {
                return;
            }

            current = delegatingHandler.InnerHandler ?? throw new ArgumentException(
                "Delegating handlers must have a configured inner handler.",
                nameof(handler));
        }
    }
}
