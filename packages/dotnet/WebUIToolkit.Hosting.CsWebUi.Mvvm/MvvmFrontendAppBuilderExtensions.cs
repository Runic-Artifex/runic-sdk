using System;
using WebUIToolkit.Hosting.CsWebUi;

namespace WebUIToolkit.Hosting.CsWebUi.Mvvm;

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
