using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;
using WebUIToolkit.Hosting.WebUi;
using WebUIToolkit.MVVM;
using WebUIToolkit.MVVM.CommunityToolkit;
using WebUIToolkit.MVVM.Html;
using WebUIToolkit.MVVM.Html.Htmx;
using WebUIToolkit.MVVM.Html.Htmx.CsWebUi;
using WebUIToolkit.Samples.AdvancedTodo.Application;
using WebUIToolkit.Samples.AdvancedTodo.Domain;

namespace WebUIToolkit.Samples.AdvancedTodo.UI;

/// <summary>Owns the compiled view, closed routes, native transport, and private web root.</summary>
internal sealed class AdvancedTodoApplicationRoot : IRootSessionFactory, IAsyncDisposable
{
    internal const string AllowedOrigin = "https://advanced-todo.native";
    internal static readonly MvvmContract Contract = new("samples.advanced-todo");
    private readonly TodoService service;
    private CwhtmlHtmxOpenedApplication<AdvancedTodoAppView, AdvancedTodoRenderModel>?
        htmxApplication;
    private CwhtmlHtmxPreparedAssets? assets;
    private TodoViewModel? model;
    private CommunityToolkitMvvmBindingAdapter<TodoViewModel>? adapter;
    private int windowConfigured;
    private int rootOpened;
    private int disposed;

    internal AdvancedTodoApplicationRoot(TodoService service)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
    }

    internal CwhtmlHtmxPreparedAssets PreparedAssets =>
        assets ??
        throw new InvalidOperationException("Prepare AdvancedTodo before creating its host.");

    internal string WebRoot => PreparedAssets.RootDirectory;

    internal CsWebUiHtmxApplication HtmxApplication =>
        htmxApplication?.Application ??
        throw new InvalidOperationException("Prepare AdvancedTodo before using it.");

    internal TodoViewModel Model =>
        model ??
        throw new InvalidOperationException("Prepare AdvancedTodo before using it.");

    internal CommunityToolkitMvvmBindingAdapter<TodoViewModel> Adapter =>
        adapter ??
        throw new InvalidOperationException("Prepare AdvancedTodo before using it.");

    internal TodoService Service => service;

    internal async ValueTask PrepareAsync(
        string staticWebRoot,
        CwhtmlHtmxAppBuilder frontend,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frontend);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (htmxApplication is not null)
        {
            throw new InvalidOperationException("AdvancedTodo is already prepared.");
        }

        model = new TodoViewModel(service);
        await model.InitializeAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CwhtmlHtmxOpenedApplication<AdvancedTodoAppView, AdvancedTodoRenderModel>
                createdApplication =
                await frontend
                    .UseHtmxAsync(
                        AdvancedTodoAppView.HtmxView,
                        Contract,
                        AllowedOrigin,
                        _ => ActivateModel(),
                        context => AdvancedTodoRenderModel.Response(Model, context),
                        view => view.ConfigureValidators(
                            "selectedId",
                        [
                            (value, _) =>
                            {
                                bool exists = Guid.TryParse(value.GetString(), out Guid id) &&
                                    Model.VisibleItems.Any(item => item.Id == id);
                                return ValueTask.FromResult<IReadOnlyList<string>>(
                                    exists ? [] : ["That task is no longer available."]);
                            },
                        ]),
                        cancellationToken)
                .ConfigureAwait(false);
            htmxApplication = createdApplication;
            AdvancedTodoAppView application = createdApplication.CreateInitialView(
                AdvancedTodoRenderModel.Initial(model));
            frontend.UseCwhtml(
                AdvancedTodoDocumentView.CwhtmlView,
                new AdvancedTodoDocumentModel(
                    application,
                    FrontendDevelopmentAssets.Resolve()));
            assets = await frontend
                .PrepareAssetsAsync(staticWebRoot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal void ConfigureWindow(WebUiWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref windowConfigured, 1) != 0)
        {
            throw new InvalidOperationException("AdvancedTodo supports exactly one native window.");
        }

        CsWebUiHtmxApplication application = htmxApplication?.Application ??
            throw new InvalidOperationException("Prepare AdvancedTodo first.");
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

    public ValueTask<IRootSession> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Volatile.Read(ref windowConfigured) == 0)
        {
            throw new InvalidOperationException(
                "CsWebUi must attach the native HTMX transport before the root opens.");
        }

        if (Interlocked.Exchange(ref rootOpened, 1) != 0)
        {
            throw new InvalidOperationException("AdvancedTodo supports exactly one root session.");
        }

        return ValueTask.FromResult<IRootSession>(new RootSession(this));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        CwhtmlHtmxOpenedApplication<AdvancedTodoAppView, AdvancedTodoRenderModel>?
            ownedApplication =
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
            try
            {
                if (ownedAssets is not null)
                {
                    await ownedAssets.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                TodoViewModel? ownedModel = Interlocked.Exchange(ref model, null);
                if (ownedModel is not null)
                {
                    await ownedModel.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private ValueTask<MvvmSessionActivation> ActivateModel()
    {
        TodoViewModel activeModel = model ??
            throw new InvalidOperationException("The advanced model is unavailable.");
        CommunityToolkitMvvmBindingAdapter<TodoViewModel> createdAdapter =
            AdvancedTodoAppView.CreateHtmxAdapter(
                activeModel,
                AdvancedTodoJsonContext.Default);
        adapter = createdAdapter;
        return ValueTask.FromResult(new MvvmSessionActivation(createdAdapter));
    }

    private sealed class RootSession(AdvancedTodoApplicationRoot owner) : IRootSession
    {
        private AdvancedTodoApplicationRoot? owner = owner;

        public ValueTask ActivateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DeactivateAsync(CancellationToken cancellationToken) => DisposeAsync();

        public async ValueTask DisposeAsync()
        {
            AdvancedTodoApplicationRoot? selected = Interlocked.Exchange(ref owner, null);
            if (selected is not null)
            {
                await selected.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

}
