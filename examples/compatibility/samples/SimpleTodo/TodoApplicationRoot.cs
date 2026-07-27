using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;
using WebUIToolkit.Hosting.WebUi;
using WebUIToolkit.MVVM;
using WebUIToolkit.MVVM.CommunityToolkit;
using WebUIToolkit.MVVM.Html;
using WebUIToolkit.MVVM.Html.Htmx;
using WebUIToolkit.MVVM.Html.Htmx.CsWebUi;

namespace WebUIToolkit.Samples.SimpleTodo;

/// <summary>
/// Owns the compiled view, closed endpoint routes, native transport, and generated web root.
/// </summary>
internal sealed class TodoApplicationRoot : IRootSessionFactory, IAsyncDisposable
{
    internal const string AllowedOrigin = "https://simple-todo.native";
    internal static readonly MvvmContract Contract = new("samples.simple-todo");
    private CsWebUiHtmxApplication? htmxApplication;
    private CwhtmlHtmxPreparedAssets? assets;
    private TodoViewModel? model;
    private CommunityToolkitMvvmBindingAdapter<TodoViewModel>? adapter;
    private string? initialDocument;
    private int windowConfigured;
    private int rootOpened;
    private int disposed;

    /// <summary>Gets the private generated root after preparation.</summary>
    internal CwhtmlHtmxPreparedAssets PreparedAssets =>
        assets ??
        throw new InvalidOperationException("Prepare the todo application before creating its host.");

    /// <summary>Gets the private generated root after preparation.</summary>
    internal string WebRoot => PreparedAssets.RootDirectory;

    internal CsWebUiHtmxApplication HtmxApplication =>
        htmxApplication ??
        throw new InvalidOperationException("Prepare the todo application before using it.");

    internal TodoViewModel Model =>
        model ??
        throw new InvalidOperationException("Prepare the todo application before using it.");

    internal CommunityToolkitMvvmBindingAdapter<TodoViewModel> Adapter =>
        adapter ??
        throw new InvalidOperationException("Prepare the todo application before using it.");

    internal string InitialDocument =>
        initialDocument ??
        throw new InvalidOperationException("Prepare the todo application before using it.");

    /// <summary>
    /// Opens the MVVM/HTMX view, assigns opaque routes, and compiles the initial document
    /// into an owned static root before CsWebUi starts serving files.
    /// </summary>
    internal async ValueTask PrepareAsync(
        string staticWebRoot,
        CwhtmlHtmxAppBuilder frontend,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frontend);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (htmxApplication is not null)
        {
            throw new InvalidOperationException("The todo application is already prepared.");
        }

        try
        {
            CsWebUiHtmxApplication createdApplication =
                await frontend
                    .OpenAsync(
                        Contract,
                        AllowedOrigin,
                        _ =>
                        {
                            model = new TodoViewModel();
                            CommunityToolkitMvvmBindingAdapter<TodoViewModel> adapter =
                                TodoAppView.CreateHtmxAdapter(
                                    model,
                                    TodoJsonContext.Default);
                            this.adapter = adapter;
                            return ValueTask.FromResult(new MvvmSessionActivation(adapter));
                        },
                        CreateDescriptor(),
                        cancellationToken)
                .ConfigureAwait(false);
            htmxApplication = createdApplication;
            TodoViewModel activeModel = model ??
                throw new InvalidOperationException("Opening the view did not activate its model.");
            TodoAppView application = TodoAppView.CreateHtmxView(
                TodoRenderModel.Initial(activeModel),
                createdApplication.OpenedView);
            initialDocument = "<!doctype html>" + Render(
                new TodoDocumentView(new TodoDocumentModel(
                    application,
                    FrontendDevelopmentAssets.Resolve())));
            assets = await frontend
                .PrepareAssetsAsync(staticWebRoot, initialDocument, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Attaches the sole browser-to-.NET binding when CsWebUi creates the window.</summary>
    internal void ConfigureWindow(WebUiWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref windowConfigured, 1) != 0)
        {
            throw new InvalidOperationException("SimpleTodo supports exactly one native window.");
        }

        CsWebUiHtmxApplication application = htmxApplication ??
            throw new InvalidOperationException(
                "Prepare the todo application before creating a window.");
        try
        {
            application.AttachWindow(window);
        }
        catch
        {
            Volatile.Write(ref windowConfigured, 0);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask<IRootSession> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Volatile.Read(ref windowConfigured) == 0)
        {
            throw new InvalidOperationException(
                "CsWebUi must attach the native HTMX transport before the root session opens.");
        }

        if (Interlocked.Exchange(ref rootOpened, 1) != 0)
        {
            throw new InvalidOperationException("SimpleTodo supports exactly one root session.");
        }

        return ValueTask.FromResult<IRootSession>(new RootSession(this));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        CsWebUiHtmxApplication? ownedApplication =
            Interlocked.Exchange(ref htmxApplication, null);
        CwhtmlHtmxPreparedAssets? ownedAssets = Interlocked.Exchange(ref assets, null);
        try
        {
            if (ownedApplication is not null)
            {
                await ownedApplication.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (ownedAssets is not null)
            {
                await ownedAssets.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private HtmxViewDescriptor CreateDescriptor()
    {
        TodoViewModel ActiveModel() =>
            model ?? throw new InvalidOperationException("The todo model is not active.");

        return TodoAppView
            .ConfigureHtmx(
                Contract,
                context => TodoRenderModel.Response(ActiveModel(), context))
            .ConfigureValidators(
                "selectedId",
            [
                (value, _) =>
                {
                    string? selectedId = value.GetString();
                    bool exists = Guid.TryParse(selectedId, out Guid id) &&
                        ActiveModel().Items.Any(item => item.Id == id);
                    IReadOnlyList<string> messages = exists
                        ? []
                        : ["That task is no longer available."];
                    return ValueTask.FromResult(messages);
                },
            ])
            .Build();
    }

    private static string Render(TodoDocumentView view)
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new Utf8HtmlWriter(output);
        view.Render(ref writer, new TemplateContext(CultureInfo.InvariantCulture));
        writer.Complete();
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    private sealed class RootSession(TodoApplicationRoot owner) : IRootSession
    {
        private TodoApplicationRoot? owner = owner;

        public ValueTask ActivateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DeactivateAsync(CancellationToken cancellationToken) => DisposeAsync();

        public async ValueTask DisposeAsync()
        {
            TodoApplicationRoot? selected = Interlocked.Exchange(ref owner, null);
            if (selected is not null)
            {
                await selected.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

}
