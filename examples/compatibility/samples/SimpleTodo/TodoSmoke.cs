using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RunicToolkit.MVVM;
using RunicMarkup.RunicToolkit.Htmx;
using RunicMarkup.RunicToolkit.Htmx.CsWebUi;

namespace RunicToolkit.Samples.SimpleTodo;

/// <summary>Out-of-process-style transport and asset checks for the sample.</summary>
internal static class TodoSmoke
{
    private static readonly string[] RequiredAssetPaths =
    [
        "cwhtml.css",
        "cwhtml.js",
        "runic-toolkit.assets.json",
    ];

    internal static async Task<int> RunAsync(TodoApplication root)
    {
        ArgumentNullException.ThrowIfNull(root);
        CsWebUiHtmxApplication application = root.HtmxApplication;
        HtmxOpenedView view = application.OpenedView;
        TodoViewModel model = root.ViewModel;
        TodoAppView.HtmxRoutes routes = TodoAppView.CreateHtmxRoutes(view);

        CaptureTransport invalid = await SendAsync(
            application,
            Request(
                view,
                routes.Add,
                view.Revision,
                [new HtmxFormValue("title", "x")]));
        CaptureTransport added = await SendAsync(
            application,
            Request(
                view,
                routes.Add,
                view.Revision,
                [new HtmxFormValue("title", "Run the compiled desktop sample")]));
        TodoItem? addedItem = model.Items.FirstOrDefault(
            static item => item.Title == "Run the compiled desktop sample");
        CaptureTransport toggled = await SendAsync(
            application,
            Request(
                view,
                routes.Toggle,
                Revision(added),
                [new HtmxFormValue("selectedId", addedItem?.Id.ToString("D") ?? string.Empty)]));
        CaptureTransport removed = await SendAsync(
            application,
            Request(
                view,
                routes.Remove,
                Revision(toggled),
                [new HtmxFormValue("selectedId", addedItem?.Id.ToString("D") ?? string.Empty)]));

        await root.PreparedAssets.Provider
            .ValidateAsync(CancellationToken.None)
            .ConfigureAwait(false);

        string[] forbiddenBindings =
        [
            string.Concat("todo", "Snapshot"),
            string.Concat("todo", "Add"),
            string.Concat("todo", "Toggle"),
            string.Concat("todo", "Remove"),
        ];
        bool generatedAssetsAreClean = Directory
            .EnumerateFiles(root.WebRoot, "*", SearchOption.AllDirectories)
            .Where(static path =>
                Path.GetExtension(path) is ".html" or ".js" or ".mjs")
            .Select(File.ReadAllText)
            .All(content => forbiddenBindings.All(
                binding => !content.Contains(binding, StringComparison.Ordinal)));
        string assemblyPath = Path.Combine(
            AppContext.BaseDirectory,
            "RunicToolkit.Samples.SimpleTodo.dll");
        bool applicationAssemblyIsClean = !File.Exists(assemblyPath);
        if (!applicationAssemblyIsClean)
        {
            byte[] applicationAssembly = await File.ReadAllBytesAsync(assemblyPath)
                .ConfigureAwait(false);
            applicationAssemblyIsClean = forbiddenBindings.All(binding =>
                applicationAssembly.AsSpan().IndexOf(Encoding.UTF8.GetBytes(binding)) < 0);
        }

        bool localAssetsPresent = RequiredAssetPaths.All(
            relativePath => File.Exists(Path.Combine(root.WebRoot, relativePath)));
        bool collectionBindingPresent = root.Adapter.Metadata.Any(static metadata =>
            metadata.MemberId == TodoAppView.HtmxCollections.Items.MemberId &&
            metadata.Kind == MvvmBindingMemberKind.Collection);
        string initialDocument = await File.ReadAllTextAsync(
            Path.Combine(root.WebRoot, "index.html"));
        string generatedRoot = root.WebRoot;
        await root.DisposeAsync().ConfigureAwait(false);
        bool generatedRootRemoved = !Directory.Exists(generatedRoot);
        bool passed =
            initialDocument.StartsWith("<!doctype html><html", StringComparison.Ordinal) &&
            (initialDocument.Contains("cwhtml.js", StringComparison.Ordinal) ||
                initialDocument.Contains("/@vite/client", StringComparison.Ordinal)) &&
            initialDocument.Contains(routes.Add, StringComparison.Ordinal) &&
            initialDocument.Contains(routes.Toggle, StringComparison.Ordinal) &&
            initialDocument.Contains(routes.Remove, StringComparison.Ordinal) &&
            invalid.StatusCode == 200 &&
            invalid.Body.Contains("between 2 and 80 characters", StringComparison.Ordinal) &&
            added.StatusCode == 200 &&
            added.Body.Contains("Run the compiled desktop sample", StringComparison.Ordinal) &&
            toggled.StatusCode == 200 &&
            toggled.Body.Contains("task completed", StringComparison.Ordinal) &&
            removed.StatusCode == 200 &&
            !removed.Body.Contains("Run the compiled desktop sample", StringComparison.Ordinal) &&
            generatedAssetsAreClean &&
            applicationAssemblyIsClean &&
            localAssetsPresent &&
            collectionBindingPresent &&
            generatedRootRemoved &&
            StringComparer.Ordinal.Equals(
                CsWebUiHtmxTransport.BindingName,
                "runic-toolkitHtmx");
        Console.WriteLine(passed
            ? "SimpleTodo compiled native HTMX smoke test passed."
            : "SimpleTodo compiled native HTMX smoke test failed.");
        if (!passed)
        {
            Console.Error.WriteLine(
                $"invalid={invalid.StatusCode}:{invalid.Body}{Environment.NewLine}" +
                $"added={added.StatusCode}:{added.Body}{Environment.NewLine}" +
                $"toggled={toggled.StatusCode}:{toggled.Body}{Environment.NewLine}" +
                $"removed={removed.StatusCode}:{removed.Body}");
        }

        return passed ? 0 : 1;
    }

    private static HtmxEndpointRequest Request(
        HtmxOpenedView view,
        string route,
        long revision,
        IReadOnlyList<HtmxFormValue> form) =>
        new(
            "POST",
            route,
            isHtmx: true,
            TodoApplicationRoot.AllowedOrigin,
            view.SessionCookie,
            view.AntiForgeryToken,
            view.AntiForgeryToken,
            view.Capability,
            revision,
            form,
            bodyByteCount: form.Sum(static value =>
                Encoding.UTF8.GetByteCount(value.Name) +
                Encoding.UTF8.GetByteCount(value.Value)),
            CultureInfo.InvariantCulture,
            Guid.NewGuid());

    private static async Task<CaptureTransport> SendAsync(
        CsWebUiHtmxApplication application,
        HtmxEndpointRequest request)
    {
        var capture = new CaptureTransport();
        await application.HandleAsync(request, capture).ConfigureAwait(false);
        return capture;
    }

    private static long Revision(CaptureTransport response) =>
        long.Parse(
            response.Headers["X-WebUI-Revision"],
            NumberStyles.None,
            CultureInfo.InvariantCulture);

    private sealed class CaptureTransport : IHtmxEndpointTransport
    {
        private HtmxEndpointResponse? _response;

        internal int StatusCode => _response?.StatusCode ?? 0;

        internal string Body => _response is null
            ? string.Empty
            : Encoding.UTF8.GetString(_response.Body.Span);

        internal IReadOnlyDictionary<string, string> Headers =>
            _response?.Headers ?? new Dictionary<string, string>();

        public ValueTask WriteAsync(
            HtmxEndpointResponse value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _response = value;
            return ValueTask.CompletedTask;
        }
    }
}
