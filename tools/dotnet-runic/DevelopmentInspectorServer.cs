using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

/// <summary>
/// Receives payload-free browser inspector events on a random loopback URL and
/// renders only its fixed, bounded diagnostic vocabulary in the dev terminal.
/// </summary>
internal sealed partial class DevelopmentInspectorServer : IAsyncDisposable
{
    private const int MaximumBodyCharacters = 16_384;
    private const int MaximumRenderedBodyCharacters = 1_048_576;
    private const int MaximumRenderedFragmentCharacters = 262_144;
    private const int MaximumRenderedFragments = 64;
    private const string RenderedFragmentsContract =
        "runic-toolkit.frontend-compiler.rendered-fragments/1.0";
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;
    private readonly string _projectDirectory;

    private DevelopmentInspectorServer(
        HttpListener listener,
        Uri endpoint,
        Uri renderedFragmentsEndpoint,
        string projectDirectory)
    {
        _listener = listener;
        Endpoint = endpoint;
        RenderedFragmentsEndpoint = renderedFragmentsEndpoint;
        _projectDirectory = projectDirectory;
        RenderedFragmentsSnapshotPath = Path.Combine(
            _projectDirectory,
            "obj",
            "RunicToolkit",
            "frontend-compiler-rendered-fragments.json");
        DeleteRenderedFragmentsSnapshot();
        _loop = ListenAsync(_shutdown.Token);
    }

    internal Uri Endpoint { get; }

    internal Uri RenderedFragmentsEndpoint { get; }

    internal string RenderedFragmentsSnapshotPath { get; }

    internal static DevelopmentInspectorServer Start(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        int port = ReserveLoopbackPort();
        string token = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var origin = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
        var endpoint = new Uri(origin, $"{token}/events");
        var renderedFragmentsEndpoint = new Uri(origin, $"{token}/rendered-fragments");
        var listener = new HttpListener();
        listener.Prefixes.Add(origin.AbsoluteUri);
        listener.Start();
        return new DevelopmentInspectorServer(
            listener,
            endpoint,
            renderedFragmentsEndpoint,
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
            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.Ordinal))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            string? path = context.Request.Url?.AbsolutePath;
            int maximumCharacters = string.Equals(
                path,
                RenderedFragmentsEndpoint.AbsolutePath,
                StringComparison.Ordinal)
                ? MaximumRenderedBodyCharacters
                : MaximumBodyCharacters;
            if (!string.Equals(path, Endpoint.AbsolutePath, StringComparison.Ordinal)
                && !string.Equals(
                    path,
                    RenderedFragmentsEndpoint.AbsolutePath,
                    StringComparison.Ordinal))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            if (context.Request.ContentLength64 > maximumCharacters * 4L)
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
            var buffer = new char[maximumCharacters + 1];
            int length = await reader
                .ReadBlockAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (length > maximumCharacters)
            {
                context.Response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
                return;
            }

            string body = new(buffer, 0, length);
            if (string.Equals(
                    path,
                    RenderedFragmentsEndpoint.AbsolutePath,
                    StringComparison.Ordinal))
            {
                if (!TryWriteRenderedFragments(body))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return;
                }

                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
            }
            else if (TryFormat(body, out string? formatted))
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

    internal bool TryWriteRenderedFragments(string json)
    {
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                MaxDepth = 4,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !TryBoundedString(root, "contract", 64, out string contract)
            || !StringComparer.Ordinal.Equals(contract, RenderedFragmentsContract)
            || !root.TryGetProperty("fragments", out JsonElement fragments)
            || fragments.ValueKind != JsonValueKind.Array
            || fragments.GetArrayLength() > MaximumRenderedFragments)
        {
            return false;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("contract", RenderedFragmentsContract);
            writer.WriteString("capturedAtUtc", DateTimeOffset.UtcNow);
            writer.WriteStartArray("fragments");
            var handles = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement fragment in fragments.EnumerateArray())
            {
                if (fragment.ValueKind != JsonValueKind.Object
                    || !TryBoundedString(fragment, "handle", 64, out string handle)
                    || !RenderedFragmentHandle().IsMatch(handle)
                    || !handles.Add(handle)
                    || !TryBoundedString(
                        fragment,
                        "html",
                        MaximumRenderedFragmentCharacters,
                        out string html))
                {
                    return false;
                }

                writer.WriteStartObject();
                writer.WriteString("handle", handle);
                writer.WriteString("html", html);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        string? directory = Path.GetDirectoryName(RenderedFragmentsSnapshotPath);
        Directory.CreateDirectory(directory!);
        string temporary = RenderedFragmentsSnapshotPath + ".tmp." +
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            File.WriteAllBytes(temporary, stream.ToArray());
            File.Move(temporary, RenderedFragmentsSnapshotPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return true;
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

        var output = new StringBuilder("[bridge] #")
            .Append(sequence.ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(direction)
            .Append(' ')
            .Append(kind);
        Append(root, output, "commandTag", 128, " ");
        Append(root, output, "handler", 512, " \u2190 ");
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

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex RenderedFragmentHandle();

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
            DeleteRenderedFragmentsSnapshot();
            _shutdown.Dispose();
        }
    }

    private void DeleteRenderedFragmentsSnapshot()
    {
        try
        {
            File.Delete(RenderedFragmentsSnapshotPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
