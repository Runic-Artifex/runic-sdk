using System.Text;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// blobs/1 (specs/application/README.md, "Blobs and uploads") through the headless test host.
internal sealed class BlobTests
{
    private const string Route = "blob";

    [Test]
    public async Task AStateBlobCrossesAsAUrlThatServesItsContent()
    {
        var model = new BlobViewModel();
        using var host = Host(model);

        var avatar = Avatar(host);
        Require(avatar.GetProperty("type").GetString() == "image/png" && avatar.GetProperty("size").GetInt64() == 8
            && avatar.GetProperty("name").GetString() == "avatar.png", $"The blob's metadata is wrong: {avatar}");
        var url = avatar.GetProperty("url").GetString()!;
        Require(url.StartsWith("http://runic.test/_runic/blobs/", StringComparison.Ordinal) && url.Length > 40, $"The URL is not a capability: {url}");
        // The same instance keeps its URL from one state to the next.
        Require(Avatar(host).GetProperty("url").GetString() == url, "The blob's URL changed without a change of the blob.");

        var served = await host.ReadBlobAsync(url);
        Require(served.StatusCode == 200 && Encoding.UTF8.GetString(served.Content) == "avatar-1", $"The blob was not served: {served.StatusCode}");
        Require(served.ContentType == "image/png", $"Content-Type: {served.ContentType}");
        Require(served.ContentDisposition == "inline; filename=\"avatar.png\"; filename*=UTF-8''avatar.png", $"Content-Disposition: {served.ContentDisposition}");
        Require(served.Headers["Cache-Control"] == "private, max-age=31536000, immutable", "The blob is not cacheable as immutable.");
        Require(served.Headers["X-Content-Type-Options"] == "nosniff" && served.Headers["Content-Security-Policy"].Contains("sandbox", StringComparison.Ordinal),
            "A blob runs as active content in the application's origin.");
        Require(served.Headers["Accept-Ranges"] == "bytes" && served.Headers["Content-Length"] == "8", "A seekable blob does not announce ranges.");

        var range = await host.ReadBlobAsync(url, range: "bytes=2-4");
        Require(range.StatusCode == 206 && Encoding.UTF8.GetString(range.Content) == "ata" && range.Headers["Content-Range"] == "bytes 2-4/8",
            $"A range was not answered: {range.StatusCode} {Encoding.UTF8.GetString(range.Content)}");
        Require((await host.ReadBlobAsync(url, range: "bytes=20-")).StatusCode == 416, "An unsatisfiable range was answered.");
        var head = await host.ReadBlobAsync(url, head: true);
        Require(head.StatusCode == 200 && head.Content.Length == 0 && head.Headers["Content-Length"] == "8", "HEAD sent a body or no length.");

        // A new blob replaces the member's: the old URL stops working.
        model.Avatar = RunicBlob.FromBytes("avatar-2"u8.ToArray(), "image/png");
        var next = Avatar(host).GetProperty("url").GetString()!;
        Require(next != url, "A new blob kept the old URL.");
        Require((await host.ReadBlobAsync(url)).StatusCode == 404, "A replaced blob is still served.");
        Require(Encoding.UTF8.GetString((await host.ReadBlobAsync(next)).Content) == "avatar-2", "The new blob is not served.");
        model.Avatar = null;
        Require(Avatar(host).ValueKind == JsonValueKind.Null, "A null blob was not written as null.");
        Require((await host.ReadBlobAsync(next)).StatusCode == 404 && host.ServedBlobCount == 0, "A cleared blob is still served.");
        Require((await host.ReadBlobAsync("http://runic.test/_runic/blobs/../../etc/passwd")).StatusCode == 404, "A path was resolved.");
    }

    [Test]
    public void ABlobPropertyIsReadOnlyForThePage()
    {
        using var host = Host(new BlobViewModel());
        var reply = host.Transport.Set(Route, "avatar", """{"url":"x","type":"image/png","size":null,"name":null}""");
        Require(reply.Contains("\"ok\":false", StringComparison.Ordinal), $"The page set a blob: {reply}");
    }

    [Test]
    public async Task ACommandResultBlobIsServedUntilTheWindowCloses()
    {
        var host = Host(new BlobViewModel());
        string url;
        try
        {
            // A result reaches the page through its operation (start, then status).
            var operation = await host.Root.StartAsync(vm => vm.ExportCommand, null, "export-1");
            Require(operation.Admission == RunicOperationAdmissionKind.Accepted, $"Export was not accepted: {operation.AdmissionJson}");
            var completion = await operation.WaitAsync();
            Require(completion.Kind == RunicOperationStatusKind.Succeeded, $"Export did not succeed: {completion.Json}");
            using var reply = JsonDocument.Parse(completion.Json);
            var result = reply.RootElement.GetProperty("result");
            url = result.GetProperty("url").GetString()!;
            var served = await host.ReadBlobAsync(url);
            Require(served.StatusCode == 200 && Encoding.UTF8.GetString(served.Content) == "report", "The result blob was not served.");
            Require(served.ContentDisposition!.StartsWith("attachment; ", StringComparison.Ordinal), $"Content-Disposition: {served.ContentDisposition}");
        }
        finally { host.Dispose(); }
        Require(host.ServedBlobCount == 0, "The window's blobs outlived its close.");
    }

    [Test]
    public async Task AnUploadReachesItsCommandOnceOnItsOwnConnection()
    {
        var model = new BlobViewModel();
        using var host = Host(model);
        var upload = await host.UploadAsync("hello, upload"u8.ToArray(), "../notes/a<b>.txt", "text/plain");
        Require(upload.FileName == "ab.txt" && upload.Length == 13 && host.PendingUploadCount == 1, $"The upload was not stored: {upload.FileName}");
        var input = Wire(upload);

        // Another connection of the window cannot use it.
        var foreign = await host.Transport.CommandAsync(Route, "attach", input, connectionKey: "connection-2");
        Require(foreign.Contains("\"reason\":\"invalid-input\"", StringComparison.Ordinal), $"Another connection used the upload: {foreign}");

        var reply = await host.Transport.CommandAsync(Route, "attach", input);
        Require(reply.Contains("\"ok\":true", StringComparison.Ordinal) && model.LastText == "hello, upload" && model.LastName == "ab.txt",
            $"The command did not read the upload: {reply}");
        Require(host.PendingUploadCount == 0, "A claimed upload stayed in the window.");
        // A claimed upload is gone: a second command with it is invalid input.
        var again = await host.Transport.CommandAsync(Route, "attach", input);
        Require(again.Contains("\"reason\":\"invalid-input\"", StringComparison.Ordinal), $"A claimed upload was claimed again: {again}");
        // The attachment's preview is a blob of the collection.
        using var state = host.Transport.SnapshotState(Route);
        var preview = state.RootElement.GetProperty("state").GetProperty("attachments")[0].GetProperty("preview");
        Require(Encoding.UTF8.GetString((await host.ReadBlobAsync(preview.GetProperty("url").GetString()!)).Content) == "hello, upload",
            "A blob in a DTO was not served.");
        // An unknown id is invalid input too.
        var unknown = await host.Transport.CommandAsync(Route, "attach", """{"id":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""");
        Require(unknown.Contains("\"reason\":\"invalid-input\"", StringComparison.Ordinal), $"An unknown upload was accepted: {unknown}");
    }

    [Test]
    public async Task AnUploadOverTheLimitIsRefusedAndAClosedConnectionDeletesItsUploads()
    {
        using var host = Host(new BlobViewModel(), maximumUploadBytes: 1_024);
        var refused = false;
        try { await host.UploadAsync(new byte[2_048]); }
        catch (InvalidOperationException error) when (error.Message.Contains("limit", StringComparison.Ordinal)) { refused = true; }
        Require(refused && host.PendingUploadCount == 0, "An upload over the limit was stored.");

        host.Transport.Hello();
        var upload = await host.UploadAsync(new byte[100]);
        Require(host.PendingUploadCount == 1 && !upload.IsClaimed, "The upload was not stored.");
        host.Transport.CloseConnection();
        Require(host.PendingUploadCount == 0 && upload.IsClaimed, "A closed connection kept its unclaimed upload.");
    }

    [Test]
    public async Task AHelloWithBlobsGetsTheConnectionsUploadGrant()
    {
        using var host = Host(new BlobViewModel());
        var hello = await host.Transport.RequestAsync(
            """{"kind":"hello","protocol":4,"session":"s-blobs","client":{"name":"t","version":"1"},"features":["blobs/1"]}""",
            "connection-blobs");
        using var reply = JsonDocument.Parse(hello);
        var root = reply.RootElement;
        Require(root.GetProperty("features").EnumerateArray().Any(feature => feature.GetString() == "blobs/1"), $"blobs/1 is not listed: {hello}");
        var blobs = root.GetProperty("blobs");
        Require(blobs.GetProperty("upload").GetString() == "http://runic.test/_runic/upload" && blobs.GetProperty("token").GetString()!.Length == 32
            && blobs.GetProperty("maximumBytes").GetInt64() == 256L * 1_048_576, $"The grant is wrong: {blobs}");

        var plain = await host.Transport.RequestAsync(
            """{"kind":"hello","protocol":4,"session":"s-plain","client":{"name":"t","version":"1"},"features":[]}""", "connection-plain");
        Require(!plain.Contains("\"blobs\"", StringComparison.Ordinal), $"A connection without blobs/1 got a grant: {plain}");
    }

    [Test]
    public async Task AnUploadForUnitTestsReadsAndSavesItsBytes()
    {
        var upload = RunicUpload.FromBytes("bytes"u8.ToArray(), "dir/x.bin");
        Require(upload.FileName == "x.bin" && upload.ContentType == "application/octet-stream", "FromBytes did not reduce the name.");
        var path = Path.Combine(Path.GetTempPath(), $"runic-upload-test-{Guid.NewGuid():N}");
        try
        {
            await upload.SaveAsAsync(path);
            Require(await File.ReadAllTextAsync(path) == "bytes", "SaveAsAsync wrote other bytes.");
            var claimed = false;
            try { await upload.OpenReadAsync(); }
            catch (InvalidOperationException) { claimed = true; }
            Require(claimed && upload.IsClaimed, "An upload was claimed twice.");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task AReactiveUiCommandTakesAnUploadAndShowsABlob()
    {
        await using var context = new RunicModelContext();
        var model = new ReactiveBlobViewModel(context);
        using var contextLease = RunicModelContextRegistry.Shared.Bind(context, model);
        using var host = new RunicWindowTestHost<ReactiveBlobViewModel>(model,
            (transport, content, vm) => new ReactiveBlobBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "reactiveBlob", ViewLocator = new TestViewLocator() });
        var upload = await host.UploadAsync("reactive"u8.ToArray(), "r.txt", "text/plain");
        var operation = await host.Root.StartAsync(vm => vm.AttachCommand, Wire(upload), "attach-1");
        Require(operation.Admission == RunicOperationAdmissionKind.Accepted, $"Attach was not accepted: {operation.AdmissionJson}");
        var completion = await operation.WaitAsync();
        Require(completion.Kind == RunicOperationStatusKind.Succeeded, $"Attach did not succeed: {completion.Json}");
        using var state = host.Transport.SnapshotState("reactiveBlob");
        var preview = state.RootElement.GetProperty("state").GetProperty("preview");
        var served = await host.ReadBlobAsync(preview.GetProperty("url").GetString()!);
        Require(Encoding.UTF8.GetString(served.Content) == "REACTIVE" && preview.GetProperty("name").GetString() == "r.txt",
            $"The ReactiveUI blob was not served: {preview}");
        var types = File.ReadAllText(Path.Combine(GeneratedTypeScriptDirectory(), "reactiveBlob.ts"));
        Require(types.Contains("readonly preview: import(\"@runic-artifex/views\").RunicBlob | null;", StringComparison.Ordinal)
            && types.Contains("upload(content: Blob", StringComparison.Ordinal), $"The ReactiveUI client lacks the blob types:\n{types}");
    }

    private static string GeneratedTypeScriptDirectory()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx")))
                return Path.Combine(directory.FullName, "tests", "dotnet", "Runic.Application.Testing.Tests", "obj", "bridge-frontend", "generated");
        throw new InvalidOperationException("Could not locate the Runic SDK workspace root.");
    }

    private static JsonElement Avatar(RunicWindowTestHost<BlobViewModel> host)
    {
        using var state = host.Transport.SnapshotState(Route);
        return state.RootElement.GetProperty("state").GetProperty("avatar").Clone();
    }

    private static string Wire(RunicUpload upload) =>
        $$"""{"id":"{{upload.Id}}","name":"{{upload.FileName}}","type":"{{upload.ContentType}}","size":{{upload.Length}}}""";

    private static RunicWindowTestHost<BlobViewModel> Host(BlobViewModel model, long? maximumUploadBytes = null) =>
        new(model, (transport, content, vm) => new BlobBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = Route, ViewLocator = new TestViewLocator(), MaximumUploadBytes = maximumUploadBytes });

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
