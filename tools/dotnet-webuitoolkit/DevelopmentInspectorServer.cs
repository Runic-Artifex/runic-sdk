using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.DotNet.WebUIToolkit;

/// <summary>
/// Receives payload-free browser inspector events on a random loopback URL and
/// renders only its fixed, bounded diagnostic vocabulary in the dev terminal.
/// </summary>
internal sealed class DevelopmentInspectorServer : IAsyncDisposable
{
    private const int MaximumBodyCharacters = 16_384;
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;
    private readonly string _projectDirectory;

    private DevelopmentInspectorServer(
        HttpListener listener,
        Uri endpoint,
        string projectDirectory)
    {
        _listener = listener;
        Endpoint = endpoint;
        _projectDirectory = projectDirectory;
        _loop = ListenAsync(_shutdown.Token);
    }

    internal Uri Endpoint { get; }

    internal static DevelopmentInspectorServer Start(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        int port = ReserveLoopbackPort();
        string token = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var origin = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
        var endpoint = new Uri(origin, $"{token}/events");
        var listener = new HttpListener();
        listener.Prefixes.Add(origin.AbsoluteUri);
        listener.Start();
        return new DevelopmentInspectorServer(
            listener,
            endpoint,
            Path.GetFullPath(projectDirectory));
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _ = HandleAsync(context, cancellationToken);
        }
    }

    private async Task HandleAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            if (!string.Equals(
                    context.Request.Url?.AbsolutePath,
                    Endpoint.AbsolutePath,
                    StringComparison.Ordinal)
                || !string.Equals(context.Request.HttpMethod, "POST", StringComparison.Ordinal))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            if (context.Request.ContentLength64 > MaximumBodyCharacters * 4L)
            {
                context.Response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
                return;
            }

            using var reader = new StreamReader(
                context.Request.InputStream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1_024,
                leaveOpen: false);
            var buffer = new char[MaximumBodyCharacters + 1];
            int length = await reader
                .ReadBlockAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (length > MaximumBodyCharacters)
            {
                context.Response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
                return;
            }

            string body = new(buffer, 0, length);
            if (TryFormat(body, out string? formatted))
            {
                Console.WriteLine(formatted);
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or JsonException
            or InvalidOperationException)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
        }
        finally
        {
            context.Response.Close();
        }
    }

    internal bool TryFormat(string json, out string? formatted)
    {
        formatted = null;
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                MaxDepth = 4,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !TryBoundedString(root, "direction", 16, out string direction)
            || direction is not ("client" or "host" or "runtime")
            || !TryBoundedString(root, "kind", 64, out string kind)
            || !TryPositiveInteger(root, "sequence", out int sequence))
        {
            return false;
        }

        var output = new StringBuilder("[mvvm] #")
            .Append(sequence.ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(direction)
            .Append(' ')
            .Append(kind);
        Append(root, output, "memberName", 128, " ");
        Append(root, output, "sourceMember", 512, " \u2190 ");
        Append(root, output, "revision", 32, " r");
        Append(root, output, "bytes", 32, " ", "B");
        Append(root, output, "durationMilliseconds", 32, " ", "ms");
        Append(root, output, "outcome", 128, " ");
        if (root.TryGetProperty("source", out JsonElement source)
            && TrySource(source, out string? sourceText))
        {
            output.Append(' ').Append(sourceText);
        }

        formatted = output.ToString();
        return true;
    }

    private bool TrySource(JsonElement source, out string? text)
    {
        text = null;
        if (source.ValueKind != JsonValueKind.Object
            || !TryBoundedString(source, "file", 1_024, out string file)
            || !TryPositiveInteger(source, "line", out int line)
            || !TryPositiveInteger(source, "column", out int column))
        {
            return false;
        }

        string candidate = Path.GetFullPath(file, _projectDirectory);
        string relative = Path.GetRelativePath(_projectDirectory, candidate);
        if (relative == ".."
            || relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            return false;
        }

        text = string.Concat(
            candidate,
            ":",
            line.ToString(CultureInfo.InvariantCulture),
            ":",
            column.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    private static void Append(
        JsonElement root,
        StringBuilder output,
        string property,
        int maximumLength,
        string prefix,
        string suffix = "")
    {
        if (root.TryGetProperty(property, out JsonElement value))
        {
            string text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                _ => string.Empty,
            };
            if (text.Length > 0 && text.Length <= maximumLength)
            {
                output.Append(prefix).Append(text).Append(suffix);
            }
        }
    }

    private static bool TryBoundedString(
        JsonElement root,
        string property,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(property, out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return value.Length > 0 && value.Length <= maximumLength;
    }

    private static bool TryPositiveInteger(
        JsonElement root,
        string property,
        out int value)
    {
        value = 0;
        return root.TryGetProperty(property, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value)
            && value > 0;
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Close();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (HttpListenerException)
        {
        }
        finally
        {
            _shutdown.Dispose();
        }
    }
}
