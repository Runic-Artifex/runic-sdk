using System.Globalization;
using System.Text;
using CsWebUi;
using Runic.Assets;

namespace Runic.Application.Views.CsWebUi;

// Serves an asset source, such as the build's embedded frontend archive, through a WebUI window's file handler instead
// of a root folder, without extracting it (#726).
internal static class CsWebUiAssets
{
    /// <summary>Throws <see cref="FileNotFoundException"/> when <paramref name="source"/> does not declare the start page.</summary>
    internal static void RequireStartPage(IAssetSource source, string startPage)
    {
        var page = AssetPath.Normalize(string.IsNullOrEmpty(startPage) ? "index.html" : startPage);
        if (!source.Manifest.TryGetAsset(page, out _))
            throw new FileNotFoundException($"The frontend assets have no start page '{page}'.", page);
    }

    /// <summary>
    /// Answers every request of <paramref name="window"/> that WebUI does not answer itself (<c>webui.js</c>) from
    /// <paramref name="source"/>, and keeps WebUI's authentication cookie, which installing a file handler turns off.
    /// </summary>
    internal static void Serve(WebUiWindow window, IAssetSource source)
    {
        window.SetFileHandler(path => Respond(source, path));
        // WebUI disables its authentication cookie process-wide when a window installs a file handler, since it cannot
        // know whether the handler serves another origin. This one serves the window's own origin, so the cookie that
        // binds the window to the first browser that loaded webui.js keeps working as with SetRootFolder.
        WebUiApplication.SetConfiguration(WebUiConfiguration.UseCookies, true);
    }

    internal static WebUiFileHandlerResult Respond(IAssetSource source, string path)
    {
        // An explicit 404 instead of NotHandled: WebUI would otherwise serve the path from its root folder, the
        // process's working directory.
        if (!source.Manifest.TryResolveRequestPath(path, options: null, out var descriptor)) return Status(404, "Not Found");
        try
        {
            using var snapshot = source.OpenAsync(descriptor.RelativePath).AsTask().GetAwaiter().GetResult();
            if (!StringComparer.Ordinal.Equals(snapshot.Descriptor.EntityTag, descriptor.EntityTag) ||
                snapshot.Descriptor.Length != descriptor.Length)
                return Status(500, "Internal Server Error");
            var header = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"HTTP/1.1 200 OK\r\nContent-Type: {WithUtf8Charset(descriptor.MediaType)}\r\nContent-Length: {descriptor.Length}\r\nETag: {descriptor.EntityTag}\r\nCache-Control: {descriptor.CacheControl}\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n"));
            var response = new byte[checked(header.Length + (int)descriptor.Length)];
            header.CopyTo(response, 0);
            snapshot.Content.ReadExactly(response, header.Length, response.Length - header.Length);
            return WebUiFileHandlerResult.FromResponse(response);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Status(500, "Internal Server Error");
        }
    }

    private static WebUiFileHandlerResult Status(int code, string reason) =>
        WebUiFileHandlerResult.FromResponse(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"HTTP/1.1 {code} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")));

    // Web build output is UTF-8; the same rule as the Desktop and ASP.NET Core adapters of Runic Assets.
    private static string WithUtf8Charset(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            && !mediaType.Contains("charset=", StringComparison.OrdinalIgnoreCase)
            ? mediaType + "; charset=utf-8"
            : mediaType;
}
