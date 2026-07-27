using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.MVVM;
using WebUIToolkit.MVVM.Html.Htmx;
using WebUIToolkit.MVVM.Html.Htmx.CsWebUi;
using WebUIToolkit.Samples.AdvancedTodo.Domain;

namespace WebUIToolkit.Samples.AdvancedTodo.UI;

/// <summary>Out-of-process-style transport, persistence, and asset checks.</summary>
internal static class AdvancedTodoSmoke
{
    private static readonly string[] RequiredAssetPaths =
    [
        "cwhtml.css",
        "cwhtml.js",
        "webuitoolkit.assets.json",
    ];

    internal static async Task<int> RunAsync(AdvancedTodoApplicationRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        CsWebUiHtmxApplication application = root.HtmxApplication;
        HtmxOpenedView view = application.OpenedView;
        TodoViewModel model = root.Model;
        AdvancedTodoAppView.HtmxRoutes routes =
            AdvancedTodoAppView.CreateHtmxRoutes(view);

        long revision = view.Revision;
        CaptureTransport invalid = await PostAsync(
            application,
            view,
            routes.Add,
            revision,
            [new("title", "x"), new("notes", ""), new("priority", "Normal")]);
        revision = Revision(invalid);
        CaptureTransport added = await PostAsync(
            application,
            view,
            routes.Add,
            revision,
            [
                new("title", "Persisted through an opaque route"),
                new("notes", "Rendered by compiled cwhtml"),
                new("priority", "High"),
            ]);
        revision = Revision(added);
        TodoItem? addedItem = model.VisibleItems.FirstOrDefault(
            static item => item.Title == "Persisted through an opaque route");
        CaptureTransport filtered = await PostAsync(
            application,
            view,
            routes.Filter,
            revision,
            [new("query", "opaque"), new("filter", "Active")]);
        revision = Revision(filtered);
        CaptureTransport toggled = await PostAsync(
            application,
            view,
            routes.Toggle,
            revision,
            [new("selectedId", addedItem?.Id.ToString("D") ?? "")]);
        revision = Revision(toggled);
        CaptureTransport clearFilter = await PostAsync(
            application,
            view,
            routes.Filter,
            revision,
            [new("query", ""), new("filter", "All")]);
        revision = Revision(clearFilter);
        CaptureTransport wizardStart = await PostAsync(
            application, view, routes.WizardStart, revision, []);
        revision = Revision(wizardStart);
        CaptureTransport wizardInvalid = await PostAsync(
            application,
            view,
            routes.WizardNext,
            revision,
            [new("wizardTitle", ""), new("notes", ""), new("priority", "Normal")]);
        revision = Revision(wizardInvalid);
        CaptureTransport wizardReview = await PostAsync(
            application,
            view,
            routes.WizardNext,
            revision,
            [
                new("wizardTitle", "Planned through Flow"),
                new("notes", "Review and retain this draft"),
                new("priority", "Low"),
            ]);
        revision = Revision(wizardReview);
        CaptureTransport wizardBack = await PostAsync(
            application, view, routes.WizardBack, revision, []);
        revision = Revision(wizardBack);
        CaptureTransport wizardReviewAgain = await PostAsync(
            application,
            view,
            routes.WizardNext,
            revision,
            [
                new("wizardTitle", "Planned through Flow"),
                new("notes", "Review and retain this draft"),
                new("priority", "Low"),
            ]);
        revision = Revision(wizardReviewAgain);
        CaptureTransport wizardFinish = await PostAsync(
            application, view, routes.WizardFinish, revision, []);
        revision = Revision(wizardFinish);
        CaptureTransport importStarted = await PostAsync(
            application,
            view,
            routes.Import,
            revision,
            []);
        revision = Revision(importStarted);
        await Task.Delay(150).ConfigureAwait(false);
        CaptureTransport cancelled = await PostAsync(
            application,
            view,
            routes.CancelImport,
            revision,
            []);

        IReadOnlyList<TodoItem> persisted = await root.Service
            .GetAsync(CancellationToken.None)
            .ConfigureAwait(false);
        await root.PreparedAssets.Provider
            .ValidateAsync(CancellationToken.None)
            .ConfigureAwait(false);

        string[] forbidden =
        [
            string.Concat("webui", ".call"),
            string.Concat("todo", "Snapshot"),
            string.Concat("todo", "Add"),
            string.Concat("todo", "Filter"),
            string.Concat("todo", "Toggle"),
            string.Concat("todo", "Delete"),
            string.Concat("todo", "Import"),
        ];
        bool generatedAssetsAreClean = Directory
            .EnumerateFiles(root.WebRoot, "*", SearchOption.AllDirectories)
            .Where(static path => Path.GetExtension(path) is ".html" or ".js" or ".mjs")
            .Select(File.ReadAllText)
            .All(content => forbidden.All(value =>
                !content.Contains(value, StringComparison.Ordinal)));
        string renderedRouteSurface = string.Concat(
            await File.ReadAllTextAsync(Path.Combine(root.WebRoot, "index.html")),
            added.Body,
            filtered.Body,
            wizardStart.Body,
            wizardInvalid.Body,
            wizardReview.Body,
            wizardBack.Body,
            wizardFinish.Body,
            importStarted.Body,
            cancelled.Body);
        bool routesAreOpaque = AdvancedTodoAppView.HtmxActions.All.All(handle =>
            renderedRouteSurface.Contains(
                view.ActionRoutes[handle],
                StringComparison.Ordinal));
        bool localAssetsPresent = RequiredAssetPaths.All(
            path => File.Exists(Path.Combine(root.WebRoot, path)));
        bool commandBindingsPresent = root.Adapter.Metadata.Count(static metadata =>
            metadata.Kind == MvvmBindingMemberKind.Command) == 13;
        string initialDocument = await File.ReadAllTextAsync(
            Path.Combine(root.WebRoot, "index.html"));
        string generatedRoot = root.WebRoot;
        await root.DisposeAsync().ConfigureAwait(false);
        bool generatedRootRemoved = !Directory.Exists(generatedRoot);
        bool passed =
            initialDocument.StartsWith("<!doctype html><html", StringComparison.Ordinal) &&
            (initialDocument.Contains("cwhtml.js", StringComparison.Ordinal) ||
                initialDocument.Contains("/@vite/client", StringComparison.Ordinal)) &&
            invalid.Body.Contains("between 2 and 120 characters", StringComparison.Ordinal) &&
            added.Body.Contains("Persisted through an opaque route", StringComparison.Ordinal) &&
            filtered.Body.Contains("Persisted through an opaque route", StringComparison.Ordinal) &&
            wizardInvalid.Body.Contains(
                "Give the task a title before continuing.",
                StringComparison.Ordinal) &&
            wizardReview.Body.Contains("2 Review", StringComparison.Ordinal) &&
            wizardBack.Body.Contains("Continue to review", StringComparison.Ordinal) &&
            wizardReviewAgain.Body.Contains("Review and retain this draft", StringComparison.Ordinal) &&
            wizardFinish.Body.Contains("Planned through Flow", StringComparison.Ordinal) &&
            importStarted.Body.Contains("Cancel running import", StringComparison.Ordinal) &&
            cancelled.Body.Contains("cancelled before persistence", StringComparison.Ordinal) &&
            persisted.Any(static item =>
                item.Title == "Persisted through an opaque route" &&
                item.IsCompleted) &&
            persisted.Any(static item => item.Title == "Planned through Flow") &&
            !persisted.Any(static item => item.Title == "Explore the guided creation flow") &&
            generatedAssetsAreClean &&
            routesAreOpaque &&
            localAssetsPresent &&
            commandBindingsPresent &&
            generatedRootRemoved &&
            StringComparer.Ordinal.Equals(
                CsWebUiHtmxTransport.BindingName,
                "webuitoolkitHtmx");
        Console.WriteLine(passed
            ? "Advanced ToDo compiled native HTMX self-test passed."
            : "Advanced ToDo compiled native HTMX self-test failed.");
        if (!passed)
        {
            Console.Error.WriteLine(
                $"clean={generatedAssetsAreClean}, routes={routesAreOpaque}, assets={localAssetsPresent}, bindings={commandBindingsPresent}, removed={generatedRootRemoved}, persisted={persisted.Count}{Environment.NewLine}" +
                $"invalid={invalid.StatusCode}:{invalid.Body}{Environment.NewLine}" +
                $"wizardInvalid={wizardInvalid.StatusCode}:{wizardInvalid.Body}{Environment.NewLine}" +
                $"importStarted={importStarted.StatusCode}:{importStarted.Body}{Environment.NewLine}" +
                $"cancel={cancelled.StatusCode}:{cancelled.Body}");
        }

        return passed ? 0 : 1;
    }

    private static HtmxEndpointRequest Request(
        HtmxOpenedView view,
        string method,
        string route,
        long revision,
        IReadOnlyList<HtmxFormValue> form,
        Guid? requestId = null,
        Guid? targetRequestId = null) =>
        new(
            method,
            route,
            isHtmx: true,
            AdvancedTodoApplicationRoot.AllowedOrigin,
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
            requestId,
            targetRequestId);

    private static Task<CaptureTransport> PostAsync(
        CsWebUiHtmxApplication application,
        HtmxOpenedView view,
        string route,
        long revision,
        IReadOnlyList<HtmxFormValue> form,
        Guid? requestId = null) =>
        SendAsync(application, Request(view, "POST", route, revision, form, requestId));

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
