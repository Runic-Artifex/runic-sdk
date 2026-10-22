// What each host is known not to expose. Every other check must pass. A known gap that starts to pass is reported, so
// the entry and the documentation (Runic.Desktop README, Accessibility) can be updated.
internal sealed class Expectations(IReadOnlyDictionary<string, string> gaps)
{
    internal string? Gap(string check) => gaps.TryGetValue(check, out var reason) ? reason : null;

    // WebKit on macOS posts the page's notifications (values, selection, focus, live regions) to VoiceOver, but none
    // reaches an AXObserver of the public API, on the application or on the web area. With VoiceOver running the VoiceOver
    // checks hear what these checks would: the slider's and the spin button's new values, the list box's selection,
    // the live region and the focused control.
    private const string WebKitNotifications =
        "WebKit posts the page's notifications to VoiceOver, not to an AXObserver of the public API";
    private const string HeardByVoiceOver = WebKitNotifications + "; checked by what VoiceOver says (voiceover-*)";

    // NVDA queues the window's UI Automation notification but speaks one only from the application of the focused
    // object (event_UIA_notification in NVDAObjects/UIA); the page's focus is in WebView2's browser process.
    private const string NvdaNotifications = "NVDA speaks a UI Automation notification only from the focused object's "
        + "application, and the page's focus is in WebView2's browser process";

    private const string WebKitGtkLiveRegion = "WebKitGTK sends no AT-SPI event for a changed live region (#318); announce with AnnounceAsync";

    // screenReader: the --screen-reader that runs, or null. Its speech checks are named after it (orca-*, nvda-*,
    // voiceover-*).
    internal static Expectations For(string host, string? screenReader) => new(host switch
    {
        "gtk3" or "gtk4" => new Dictionary<string, string>
        {
            ["number-range"] = "WebKitGTK exposes <input type=\"number\"> as an entry, without the Value interface",
            ["live-region"] = WebKitGtkLiveRegion,
            ["orca-live-region"] = WebKitGtkLiveRegion,
        },
        "webview2" => new Dictionary<string, string>
        {
            ["nvda-announcement-polite"] = NvdaNotifications,
            ["nvda-announcement-assertive"] = NvdaNotifications,
        },
        "wkwebview" => new Dictionary<string, string>
        {
            ["number-range"] = "WebKit exposes <input type=\"number\"> as a text field whose AXMinValue and AXMaxValue are 0",
            ["slider-change-event"] = screenReader is "voiceover" ? HeardByVoiceOver : WebKitNotifications,
            ["spinbutton-increment-event"] = screenReader is "voiceover" ? HeardByVoiceOver : WebKitNotifications,
            ["listbox-select-event"] = screenReader is "voiceover" ? HeardByVoiceOver : WebKitNotifications,
            ["live-region"] = screenReader is "voiceover" ? HeardByVoiceOver : WebKitNotifications,
            ["focus-event"] = screenReader is "voiceover" ? HeardByVoiceOver : WebKitNotifications,
            // Changed by the page while another control has focus, which VoiceOver does not speak.
            ["spinbutton-page-change-event"] = WebKitNotifications,
            ["aria-listbox-page-change-event"] = WebKitNotifications,
        },
        _ => throw new ArgumentException($"Unknown host '{host}'; use gtk3, gtk4, webview2 or wkwebview."),
    });
}
