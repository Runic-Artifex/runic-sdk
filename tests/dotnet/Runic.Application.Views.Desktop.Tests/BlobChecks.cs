using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using TUnit.Core;

// blobs/1 on a Desktop surface, over HTTP as a page uses it: a blob URL serves only the window's page (its client
// cookie), an upload needs the page's origin and its connection's token, and a command then reads the upload. The
// surface runs on the repository's default server (Kestrel) and, with -p:RunicDesktopServer=Native, on the native one.
internal sealed class BlobChecks
{
    [Test]
    public async Task ABlobAndAnUploadCrossTheSurfaceOverHttp()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellation = stop.Token;
        var services = new ServiceCollection();
        services.AddScoped<BlobModel>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, BlobModel, IDisposable>>(
            _ => (transport, content, model) => new BlobModelBridge(transport, model, content));
        await using var provider = services.BuildServiceProvider();
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new NativeDispatchFactory(),
            WaitForConnection = false,
        });
        await using var window = await TestWindows.EmbeddedHtml(provider, desktop).OpenWindowAsync<BlobWindow>();
        var host = (DesktopBridgeWindow<BlobModel>)window.Host;
        var surface = host.Surface;
        var origin = $"{surface.Url.Scheme}://{surface.Url.Authority}";

        // The page: its document request sets the window's client cookie.
        var cookies = new CookieContainer();
        using var page = new HttpClient(new HttpClientHandler { CookieContainer = cookies });
        (await page.GetAsync(surface.Url, cancellation)).EnsureSuccessStatusCode();
        using var wire = await DesktopWireClient.ConnectAsync(surface, cancellation, cookies);
        using var hello = JsonDocument.Parse(await wire.RequestAsync(
            """{"kind":"hello","protocol":4,"session":"s-blobs","client":{"name":"desktop-wire-client","version":"0"},"features":["blobs/1"]}""",
            cancellation));
        var grant = hello.RootElement.GetProperty("blobs");
        var uploadUrl = grant.GetProperty("upload").GetString()!;
        var token = grant.GetProperty("token").GetString()!;
        Check(uploadUrl == new Uri(surface.Url, "_runic/upload").AbsoluteUri, $"The upload URL is not the surface's: {uploadUrl}");

        using var opened = JsonDocument.Parse(await wire.RequestAsync("""{"kind":"open","route":"blobs","presentation":"p1"}""", cancellation));
        var url = opened.RootElement.GetProperty("state").GetProperty("avatar").GetProperty("url").GetString()!;
        Check(url.StartsWith(new Uri(surface.Url, "_runic/blobs/").AbsoluteUri, StringComparison.Ordinal), $"The blob URL is not the surface's: {url}");

        using (var blob = await page.GetAsync(url, cancellation))
        {
            Check(blob.StatusCode == HttpStatusCode.OK && await blob.Content.ReadAsStringAsync(cancellation) == "avatar",
                $"The page could not load the blob: {blob.StatusCode}");
            Check(blob.Content.Headers.ContentType?.MediaType == "image/png", $"Content-Type: {blob.Content.Headers.ContentType}");
            Check(blob.Headers.CacheControl is { Private: true, MaxAge.TotalDays: 365 } cache && cache.Extensions.Any(item => item.Name == "immutable"),
                $"Cache-Control: {blob.Headers.CacheControl}");
        }
        using (var ranged = new HttpRequestMessage(HttpMethod.Get, url))
        {
            ranged.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 3);
            using var partial = await page.SendAsync(ranged, cancellation);
            Check(partial.StatusCode == HttpStatusCode.PartialContent && await partial.Content.ReadAsStringAsync(cancellation) == "vat",
                $"A range was not served: {partial.StatusCode}");
        }
        // Another client of the machine, without the window's cookie, and another site, are refused.
        using (var stranger = new HttpClient())
            Check((await stranger.GetAsync(url, cancellation)).StatusCode == HttpStatusCode.Forbidden, "A client without the cookie loaded the blob.");
        using (var embedded = new HttpRequestMessage(HttpMethod.Get, url))
        {
            embedded.Headers.Add("Sec-Fetch-Site", "cross-site");
            Check((await page.SendAsync(embedded, cancellation)).StatusCode == HttpStatusCode.Forbidden, "Another site embedded the blob.");
        }

        // A streamed upload of 300 000 bytes: refused without the token or from another origin, then stored.
        var content = string.Concat(Enumerable.Repeat("0123456789", 30_000));
        Check((await page.SendAsync(Upload(uploadUrl, content, origin, token: null), cancellation)).StatusCode == HttpStatusCode.Forbidden,
            "An upload without the connection's token was stored.");
        Check((await page.SendAsync(Upload(uploadUrl, content, "http://attacker.example", token), cancellation)).StatusCode == HttpStatusCode.Forbidden,
            "An upload from another origin was stored.");
        using var stored = await page.SendAsync(Upload(uploadUrl, content, origin, token), cancellation);
        var answer = await stored.Content.ReadAsStringAsync(cancellation);
        Check(stored.StatusCode == HttpStatusCode.Created, $"The upload was not stored: {stored.StatusCode} {answer}");
        using var reply = JsonDocument.Parse(answer);
        var upload = reply.RootElement.GetProperty("upload");
        Check(upload.GetProperty("size").GetInt64() == content.Length && upload.GetProperty("name").GetString() == "notes ä.txt",
            $"The upload's record is wrong: {upload}");

        var command = await wire.RequestAsync(
            $$"""{"kind":"command","route":"blobs","member":"attach","input":{{upload.GetRawText()}}}""", cancellation);
        Check(command.StartsWith("{\"ok\":true", StringComparison.Ordinal), $"The command did not take the upload: {command}");
        var model = (BlobModel)window.DataContext!;
        Check(await model.Attached.Task.WaitAsync(cancellation) == content, "The command read other bytes than the page uploaded.");
    }

    private static HttpRequestMessage Upload(string url, string content, string origin, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(content))),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("X-Runic-Upload-Name", Uri.EscapeDataString("notes ä.txt"));
        if (token is not null) request.Headers.Add("X-Runic-Upload-Token", token);
        return request;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

sealed class BlobModel : INotifyPropertyChanged
{
    public RunicBlob Avatar { get; } = RunicBlob.FromBytes("avatar"u8.ToArray(), "image/png", "avatar.png");
    public TaskCompletionSource<string> Attached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public System.Windows.Input.ICommand Attach { get; } = new NoopCommand();

    public async Task AttachAsync(RunicUpload upload, CancellationToken cancellationToken)
    {
        await using var stream = await upload.OpenReadAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Attached.TrySetResult(await reader.ReadToEndAsync(cancellationToken));
    }

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

// What the generator writes for the model: the blob as a state member, the upload as the command's input.
sealed class BlobModelBridge(IBridgeTransport transport, BlobModel model, WindowContentSession content)
    : ViewModelBridge<BlobModel>(transport, model, "blobs",
        [new BridgeStateMember<BlobModel>("avatar", static (writer, vm) => BridgeWire.WriteBlob(writer, vm.Avatar))],
        [new("Avatar", "avatar", vm => vm.Avatar, null)], [],
        [new("Attach", vm => vm.Attach)
        {
            ReadInput = static element => BridgeWire.ReadUpload(element),
            ExecuteAsync = static (vm, token, argument) => vm.AttachAsync((RunicUpload)argument!, token),
            CanExecute = static (_, _) => true,
        }],
        content: content);

sealed class BlobWindow : RunicWindow<BlobModel>;
