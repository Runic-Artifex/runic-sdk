using System;
using System.IO;
using CsWebUi;

namespace WebUIToolkit.Hosting.CsWebUi;

/// <summary>Selects how CsWebUi presents a hosted application.</summary>
public enum CsWebUiPresentationMode
{
    /// <summary>Lets CsWebUi select its recommended installed browser.</summary>
    Auto,

    /// <summary>Launches a selected installed browser.</summary>
    Browser,

    /// <summary>Launches the platform's embedded WebView.</summary>
    WebView,
}

/// <summary>Contains immutable configuration for the CsWebUi browser-host adapter.</summary>
public sealed record CsWebUiAdapterOptions
{
    /// <summary>Initializes CsWebUi adapter configuration.</summary>
    /// <param name="webRoot">The local directory from which CsWebUi serves frontend files.</param>
    /// <param name="presentationMode">How the native window is presented.</param>
    /// <param name="browser">The browser selected when <paramref name="presentationMode"/> is Browser.</param>
    /// <param name="configureWindow">
    /// An optional callback that can bind CsWebUi callbacks before the window is shown.
    /// </param>
    public CsWebUiAdapterOptions(
        string webRoot,
        CsWebUiPresentationMode presentationMode = CsWebUiPresentationMode.Auto,
        WebUiBrowser browser = WebUiBrowser.AnyBrowser,
        Action<WebUiWindow>? configureWindow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webRoot);
        if (!Enum.IsDefined(presentationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(presentationMode));
        }

        if (!Enum.IsDefined(browser))
        {
            throw new ArgumentOutOfRangeException(nameof(browser));
        }

        if (presentationMode == CsWebUiPresentationMode.Browser &&
            browser is WebUiBrowser.NoBrowser or WebUiBrowser.WebView)
        {
            throw new ArgumentException(
                "Browser presentation requires an installed-browser selection.",
                nameof(browser));
        }

        WebRoot = Path.GetFullPath(webRoot);
        PresentationMode = presentationMode;
        Browser = browser;
        ConfigureWindow = configureWindow;
    }

    /// <summary>Gets the normalized absolute local web root.</summary>
    public string WebRoot { get; }

    /// <summary>Gets how the native window is presented.</summary>
    public CsWebUiPresentationMode PresentationMode { get; }

    /// <summary>Gets the browser selected for Browser presentation.</summary>
    public WebUiBrowser Browser { get; }

    /// <summary>Gets the optional native-window configuration callback.</summary>
    public Action<WebUiWindow>? ConfigureWindow { get; }
}
