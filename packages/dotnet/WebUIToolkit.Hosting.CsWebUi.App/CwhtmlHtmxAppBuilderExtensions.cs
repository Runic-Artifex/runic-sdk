using WebUIToolkit.Hosting;
using WebUIToolkit.Hosting.CsWebUi;

namespace WebUIToolkit.MVVM.Html.Htmx.CsWebUi;

/// <summary>cwhtml/HTMX-specific surface on the shared application builder.</summary>
public readonly struct CwhtmlHtmxAppBuilder
{
    private readonly WebUiAppBuilder _application;

    internal CwhtmlHtmxAppBuilder(WebUiAppBuilder application)
    {
        _application = application;
    }

    /// <summary>Registers one compiled cwhtml/HTMX native frontend.</summary>
    public WebUiAppBuilder Use(CsWebUiAppOptions options) =>
        _application.UseCsWebUi("cwhtml + HTMX", options);
}

/// <summary>Contributes compiled cwhtml/HTMX members to the common builder.</summary>
public static class CwhtmlHtmxAppBuilderExtensions
{
    extension(WebUiAppBuilder builder)
    {
        /// <summary>Gets cwhtml/HTMX-specific application configuration.</summary>
        public CwhtmlHtmxAppBuilder CwhtmlHtmx => new(builder);

        /// <summary>Registers one compiled cwhtml/HTMX native frontend.</summary>
        public WebUiAppBuilder UseCwhtmlHtmx(CsWebUiAppOptions options) =>
            new CwhtmlHtmxAppBuilder(builder).Use(options);
    }
}
