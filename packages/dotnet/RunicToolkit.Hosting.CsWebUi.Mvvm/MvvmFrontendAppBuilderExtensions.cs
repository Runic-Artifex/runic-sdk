using System;
using System.Threading.Tasks;
using RunicToolkit.Hosting.CsWebUi;
using RunicToolkit.MVVM;

namespace RunicToolkit.Hosting.CsWebUi.Mvvm;

/// <summary>One framework-specific MVVM frontend surface on the shared builder.</summary>
public readonly struct MvvmFrontendAppBuilder
{
    private readonly WebUiAppBuilder _application;

    internal MvvmFrontendAppBuilder(WebUiAppBuilder application, string name)
    {
        _application = application;
        Name = name;
    }

    /// <summary>Gets the JavaScript framework name.</summary>
    public string Name { get; }

    /// <summary>Registers this framework's native MVVM frontend.</summary>
    public WebUiAppBuilder Use(CsWebUiAppOptions options) =>
        _application.UseCsWebUi(Name, options);

    /// <summary>
    /// Creates and registers one generated-contract ViewModel application.
    /// </summary>
    public MvvmFrontendApplication CreateApplication<TModel>(
        MvvmFrontendApplicationOptions<TModel> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var registry = new MvvmSessionRegistry();
        registry.Map(options.Contract, async cancellationToken =>
        {
            TModel model = await options.ActivateModel(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                IMvvmBindingAdapter adapter = options.CreateAdapter(model);
                return model is IAsyncDisposable or IDisposable
                    ? new MvvmSessionActivation(adapter, model!)
                    : new MvvmSessionActivation(adapter);
            }
            catch
            {
                await DisposeModelAsync(model).ConfigureAwait(false);
                throw;
            }
        });

        var root = new MvvmFrontendRoot(
            options.Contract,
            registry.Build(options.Limits));
        Action<global::CsWebUi.WebUiWindow>? configureApplication =
            options.Adapter.ConfigureWindow;
        var adapterOptions = new CsWebUiAdapterOptions(
            options.Adapter.WebRoot,
            options.Adapter.PresentationMode,
            options.Adapter.Browser,
            window =>
            {
                configureApplication?.Invoke(window);
                root.AttachWindow(window);
            });
        try
        {
            _application.UseCsWebUi(
                Name,
                new CsWebUiAppOptions(
                    options.Assets,
                    root,
                    adapterOptions,
                    options.BrowserHost,
                    options.BrowserWindow,
                    options.SessionCloseTimeout,
                    options.WindowCloseTimeout));
            return new MvvmFrontendApplication(root);
        }
        catch
        {
            root.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private static async ValueTask DisposeModelAsync<TModel>(TModel model)
    {
        switch (model)
        {
            case IAsyncDisposable asynchronous:
                await asynchronous.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable synchronous:
                synchronous.Dispose();
                break;
        }
    }
}

/// <summary>Contributes framework-native frontend members to the common builder.</summary>
public static class MvvmFrontendAppBuilderExtensions
{
    extension(WebUiAppBuilder builder)
    {
        /// <summary>Gets React-specific application configuration.</summary>
        public MvvmFrontendAppBuilder React => new(builder, "React");

        /// <summary>Gets Vue-specific application configuration.</summary>
        public MvvmFrontendAppBuilder Vue => new(builder, "Vue");

        /// <summary>Gets Svelte-specific application configuration.</summary>
        public MvvmFrontendAppBuilder Svelte => new(builder, "Svelte");

        /// <summary>Gets Angular-specific application configuration.</summary>
        public MvvmFrontendAppBuilder Angular => new(builder, "Angular");

        /// <summary>Registers a React MVVM frontend.</summary>
        public WebUiAppBuilder UseReact(CsWebUiAppOptions options) =>
            new MvvmFrontendAppBuilder(builder, "React").Use(options);

        /// <summary>Registers a Vue MVVM frontend.</summary>
        public WebUiAppBuilder UseVue(CsWebUiAppOptions options) =>
            new MvvmFrontendAppBuilder(builder, "Vue").Use(options);

        /// <summary>Registers a Svelte MVVM frontend.</summary>
        public WebUiAppBuilder UseSvelte(CsWebUiAppOptions options) =>
            new MvvmFrontendAppBuilder(builder, "Svelte").Use(options);

        /// <summary>Registers an Angular MVVM frontend.</summary>
        public WebUiAppBuilder UseAngular(CsWebUiAppOptions options) =>
            new MvvmFrontendAppBuilder(builder, "Angular").Use(options);
    }
}
