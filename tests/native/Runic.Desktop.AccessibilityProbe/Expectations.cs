// What each host is known not to expose. Every other check must pass. A known gap that starts to pass is reported, so
// the entry and the documentation (Runic.Desktop README, Accessibility) can be updated.
internal sealed class Expectations(IReadOnlyDictionary<string, string> gaps)
{
    internal string? Gap(string check) => gaps.TryGetValue(check, out var reason) ? reason : null;

    internal static Expectations For(string host) => new(host switch
    {
        "gtk3" or "gtk4" => new Dictionary<string, string>
        {
            ["live-region"] = "WebKitGTK sends no AT-SPI event for a changed live region (#318); announce with AnnounceAsync",
        },
        "webview2" or "wkwebview" => new Dictionary<string, string>(),
        _ => throw new ArgumentException($"Unknown host '{host}'; use gtk3, gtk4, webview2 or wkwebview."),
    });
}
