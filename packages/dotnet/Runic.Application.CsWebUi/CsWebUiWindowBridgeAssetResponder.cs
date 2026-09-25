using System.Text;
using System.Text.Encodings.Web;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using CsWebUi;
using Runic.Application.Bridge;
using Runic.Assets;

namespace Runic.Application.CsWebUi;

/// <summary>Manifest-only asset and bootstrap responder shared by the internal Window Bridge host.</summary>
internal sealed class CsWebUiWindowBridgeAssetResponder(
    IAssetSource assets, string credential, string title, int maxRouteBytes, int maxAssetBytes)
{
    private volatile string? _readinessPath;
    private readonly byte[] _documentTicketKey = RandomNumberGenerator.GetBytes(32);
    private long _nextDocumentOrdinal;

    internal string? ReadinessPath
    {
        get => _readinessPath;
        set => _readinessPath = value;
    }

    /// <summary>
    /// Internal test-host bootstrap for fixed dispatcher endpoint descriptors.
    /// Generated presentation state will replace this fixture bridge in a
    /// later slice.
    /// </summary>
    internal Func<string>? DispatchEndpointBootstrap { get; set; }

    // The sequence orders entry responses; the MAC keeps an old document from
    // inventing a later ticket after its own callbacks have been delayed.
    private string IssueDocumentEpoch()
    {
        long ordinal = checked(Interlocked.Increment(ref _nextDocumentOrdinal));
        Span<byte> sequence = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(sequence, ordinal);
        byte[] mac = HMACSHA256.HashData(_documentTicketKey, sequence);
        return ordinal.ToString("X16", System.Globalization.CultureInfo.InvariantCulture)
            + Convert.ToHexString(mac.AsSpan(0, 8));
    }

    internal bool IsIssuedDocumentEpoch(WindowBridgeDocumentEpoch epoch)
    {
        if (epoch.Ordinal == 0 || epoch.Ordinal > (ulong)Interlocked.Read(ref _nextDocumentOrdinal)) return false;
        Span<byte> sequence = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(sequence, epoch.Ordinal);
        byte[] expected = HMACSHA256.HashData(_documentTicketKey, sequence);
        byte[] supplied = Convert.FromHexString(epoch.Value.AsSpan(16));
        return CryptographicOperations.FixedTimeEquals(expected.AsSpan(0, 8), supplied);
    }

    internal WebUiFileHandlerResult Serve(string path)
    {
        try
        {
            if (path == _readinessPath) return Response(200, "text/plain", []);
            string normalized = string.IsNullOrEmpty(path.Trim('/'))
                ? assets.Manifest.EntryPoint.RelativePath
                : AssetPath.Normalize(path.TrimStart('/'));
            if (!assets.Manifest.TryGetAsset(normalized, out var asset) || asset is null)
                return Response(404, "text/plain", []);
            if (asset.Length > maxAssetBytes) return Response(413, "text/plain", []);
            using var input = assets.OpenReadAsync(asset.RelativePath).AsTask().GetAwaiter().GetResult();
            using var output = new MemoryStream();
            byte[] buffer = new byte[16 * 1024];
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                if (output.Length + read > maxAssetBytes) return Response(413, "text/plain", []);
                output.Write(buffer, 0, read);
            }
            byte[] body = output.ToArray();
            if (asset.IsEntryPoint)
            {
                string endpointBootstrap = DispatchEndpointBootstrap?.Invoke() ?? "{\"revision\":0,\"endpoints\":{}}";
                string parsedManifest = "JSON.parse(" + WindowBridgeJson.StringLiteral(endpointBootstrap) + ")";
                string bootstrap = "<script src=\"/webui.js\"></script><script>(()=>{const manifest=" + parsedManifest + ";globalThis.runicCsWebUi={credential:'" + credential
                    + "',maxFrameBytes:" + maxRouteBytes + ",documentEpoch:'" + IssueDocumentEpoch() + "',endpointRevision:manifest.revision,endpoints:Object.assign(Object.create(null),manifest.endpoints)};globalThis.__runicBridgeEndpointHandoff=function(handoff){const bridge=globalThis.runicCsWebUi;if(!bridge||!handoff||handoff.v!==1||!Number.isSafeInteger(handoff.revision)||handoff.revision<=bridge.endpointRevision||!handoff.endpoints||typeof handoff.endpoints!=='object'||Array.isArray(handoff.endpoints))return;const endpoints=bridge.endpoints;for(const route of Object.keys(endpoints))delete endpoints[route];Object.assign(endpoints,handoff.endpoints);bridge.endpointRevision=handoff.revision;globalThis.dispatchEvent(new CustomEvent('runicbridgeendpoints',{detail:{revision:handoff.revision}}));};document.title=\"" + JavaScriptEncoder.Default.Encode(title) + "\";})();</script>";
                string html = Encoding.UTF8.GetString(body)
                    .Replace("<script src=\"/runic-desktop.js\"></script>", "", StringComparison.Ordinal)
                    .Replace("<script src=\"./runic-desktop.js\"></script>", "", StringComparison.Ordinal);
                int head = html.IndexOf('>', html.IndexOf("<head", StringComparison.OrdinalIgnoreCase) is var pos && pos >= 0 ? pos : 0);
                body = Encoding.UTF8.GetBytes(head >= 0 ? html.Insert(head + 1, bootstrap) : bootstrap + html);
            }
            return Response(200, asset.MediaType, body);
        }
        catch { return Response(404, "text/plain", []); }
    }

    private static WebUiFileHandlerResult Response(int status, string mediaType, byte[] body)
    {
        byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Response\r\nContent-Type: {mediaType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: frame-ancestors 'none'\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
        byte[] result = new byte[header.Length + body.Length];
        header.CopyTo(result, 0);
        body.CopyTo(result, header.Length);
        return WebUiFileHandlerResult.FromResponse(result);
    }
}
