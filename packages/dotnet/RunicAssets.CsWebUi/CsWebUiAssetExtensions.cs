using System;
using System.IO;
using System.Text;
using System.Threading;
using CsWebUi;

namespace RunicAssets.CsWebUi;

/// <summary>Adapts transport-neutral Runic assets to CS-WebUI delivery.</summary>
public static class CsWebUiAssetExtensions
{
    private static readonly byte[] NotFoundResponse = CreateErrorResponse(
        "404 Not Found",
        "Not Found");

    private static readonly byte[] InternalServerErrorResponse = CreateErrorResponse(
        "500 Internal Server Error",
        "Internal Server Error");

    /// <summary>Creates a direct, closed-fallback CS-WebUI handler over the current asset source.</summary>
    /// <remarks>
    /// The handler resolves the source's current manifest on every request. Stable and live assets
    /// are read directly without response caching, so a refreshed development source becomes visible
    /// without replacing the handler. Missing and invalid paths return an explicit 404 response;
    /// resolution and read failures return an explicit 500 response.
    /// </remarks>
    public static WebUiFileHandler ToWebUiFileHandler(
        this IAssetSource source,
        RunicAssetsCsWebUiOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new RunicAssetsCsWebUiOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxResponseBytes, 1);

        bool enableSinglePageApplicationFallback = options.EnableSinglePageApplicationFallback;
        int maxResponseBytes = options.MaxResponseBytes;
        return path => HandleRequest(
            source,
            path,
            enableSinglePageApplicationFallback,
            maxResponseBytes);
    }

    /// <summary>Attaches a direct Runic Assets HTTP response adapter to a CS-WebUI window.</summary>
    /// <remarks>
    /// Installing a WebUI custom file handler disables WebUI's authentication-cookie check
    /// process-wide. Keep the window private and loopback-only unless the application supplies an
    /// upstream authentication layer. WebUI serializes HTTP handler calls process-wide, so sources
    /// should complete reads promptly.
    /// </remarks>
    public static void SetRunicAssets(
        this WebUiWindow window,
        IAssetSource source,
        RunicAssetsCsWebUiOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(source);
        options ??= new RunicAssetsCsWebUiOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxResponseBytes, 1);

        window.SetFileHandler(
            source.ToWebUiFileHandler(options),
            new WebUiFileHandlerOptions { MaxResponseBytes = options.MaxResponseBytes });
    }

    private static WebUiFileHandlerResult HandleRequest(
        IAssetSource source,
        string path,
        bool enableSinglePageApplicationFallback,
        int maxResponseBytes)
    {
        try
        {
            AssetManifest manifest = source.Manifest;
            AssetDescriptor descriptor;
            if (string.IsNullOrEmpty(path) || StringComparer.Ordinal.Equals(path, "/"))
            {
                descriptor = manifest.EntryPoint;
            }
            else
            {
                string relativePath = path[0] == '/' ? path[1..] : path;
                string normalizedPath;
                try
                {
                    normalizedPath = AssetPath.Normalize(relativePath);
                }
                catch (ArgumentException)
                {
                    return WebUiFileHandlerResult.FromResponse(NotFoundResponse);
                }

                if (!manifest.TryGetAsset(normalizedPath, out AssetDescriptor? resolved)
                    || resolved is null)
                {
                    if (!enableSinglePageApplicationFallback || HasFileExtension(normalizedPath))
                    {
                        return WebUiFileHandlerResult.FromResponse(NotFoundResponse);
                    }

                    descriptor = manifest.EntryPoint;
                }
                else
                {
                    descriptor = resolved;
                }
            }

            byte[] content = ReadExactContent(source, descriptor);
            byte[] response = CreateAssetResponse(descriptor, content, maxResponseBytes);
            return WebUiFileHandlerResult.FromResponse(response);
        }
        catch
        {
            return WebUiFileHandlerResult.FromResponse(InternalServerErrorResponse);
        }
    }

    private static byte[] ReadExactContent(IAssetSource source, AssetDescriptor descriptor)
    {
        if (descriptor.Length > int.MaxValue)
        {
            throw new InvalidDataException("CS-WebUI cannot serve an asset larger than 2 GiB.");
        }

        Stream stream = source
            .OpenReadAsync(descriptor.RelativePath, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        using (stream)
        {
            var content = GC.AllocateUninitializedArray<byte>(checked((int)descriptor.Length));
            int offset = 0;
            while (offset < content.Length)
            {
                int read = stream
                    .ReadAsync(content.AsMemory(offset), CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                if (read == 0)
                {
                    throw new InvalidDataException(
                        $"Asset '{descriptor.RelativePath}' ended before its declared length.");
                }

                offset += read;
            }

            var extra = new byte[1];
            if (stream
                .ReadAsync(extra, CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult() != 0)
            {
                throw new InvalidDataException(
                    $"Asset '{descriptor.RelativePath}' exceeded its declared length.");
            }

            return content;
        }
    }

    private static byte[] CreateAssetResponse(
        AssetDescriptor descriptor,
        ReadOnlySpan<byte> content,
        int maxResponseBytes)
    {
        EnsureAscii(descriptor.MediaType, nameof(descriptor.MediaType));
        string headerText =
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {descriptor.MediaType}\r\n" +
            $"Content-Length: {content.Length}\r\n" +
            $"Cache-Control: {descriptor.CacheControl}\r\n" +
            $"ETag: {descriptor.EntityTag}\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "\r\n";
        byte[] header = Encoding.ASCII.GetBytes(headerText);
        int responseLength = checked(header.Length + content.Length);
        if (responseLength > maxResponseBytes)
        {
            throw new InvalidDataException(
                $"The complete asset response exceeds the configured {maxResponseBytes} byte limit.");
        }

        var response = GC.AllocateUninitializedArray<byte>(responseLength);
        header.CopyTo(response, 0);
        content.CopyTo(response.AsSpan(header.Length));
        return response;
    }

    private static byte[] CreateErrorResponse(string status, string message)
    {
        byte[] content = Encoding.UTF8.GetBytes(message);
        byte[] header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {content.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "\r\n");
        var response = GC.AllocateUninitializedArray<byte>(checked(header.Length + content.Length));
        header.CopyTo(response, 0);
        content.CopyTo(response, header.Length);
        return response;
    }

    private static bool HasFileExtension(string path)
    {
        int nameStart = path.LastIndexOf('/') + 1;
        int dot = path.LastIndexOf('.');
        return dot > nameStart && dot < path.Length - 1;
    }

    private static void EnsureAscii(string value, string name)
    {
        foreach (char character in value)
        {
            if (character > 0x7f)
            {
                throw new InvalidDataException($"{name} must contain only ASCII header characters.");
            }
        }
    }
}
