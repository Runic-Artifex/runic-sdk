using RunicToolkit.Hosting.CsWebUi;

namespace RunicToolkit.Hosting.CsWebUi.ApplicationBridge;

/// <summary>Contributes Application Bridge composition to the common WebUi builder.</summary>
public static class ApplicationBridgeWebUiAppBuilderExtensions
{
    extension(WebUiAppBuilder builder)
    {
        /// <summary>Creates and registers one generated-contract frontend.</summary>
        public ApplicationBridgeFrontendApplication UseApplicationBridge(
            string frontendName,
            ApplicationBridgeFrontendApplicationOptions options)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(frontendName);
            ArgumentNullException.ThrowIfNull(options);
            var root = new ApplicationBridgeFrontendRoot(options.CreateSession, options.Bridge);
            Action<global::CsWebUi.WebUiWindow>? configureWindow = options.Adapter.ConfigureWindow;
            var adapter = new CsWebUiAdapterOptions(
                options.Adapter.WebRoot,
                options.Adapter.PresentationMode,
                options.Adapter.Browser,
                window =>
                {
                    configureWindow?.Invoke(window);
                    root.AttachWindow(window);
                });
            try
            {
                builder.UseCsWebUi(
                    frontendName,
                    new CsWebUiAppOptions(
                        options.Assets,
                        root,
                        adapter,
                        options.BrowserHost,
                        options.BrowserWindow,
                        options.SessionCloseTimeout,
                        options.WindowCloseTimeout));
                return new ApplicationBridgeFrontendApplication(root);
            }
            catch
            {
                root.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }
        }
    }
}
