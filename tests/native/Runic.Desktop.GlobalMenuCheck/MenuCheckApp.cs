using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Runic.Desktop;
using Runic.Platform;

namespace GlobalMenuCheck;

internal sealed record CheckSettings(bool Gtk4, bool GlobalMenu, bool Frameless, int InitialWindows);

/// <summary>Owns the menu bar, its state and every open window; the application's menu command target.</summary>
internal sealed class MenuCheckApp(CheckSettings settings) : IDesktopMenuCommandTarget
{
    private readonly object _gate = new();
    private readonly List<CheckWindow> _windows = [];
    private readonly List<string> _log = [];
    private readonly TaskCompletionSource _allClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DesktopHost? _host;
    private int _nextWindow;

    // Menu state. The "declared" items are rebuilt with SetMenuBar after each change; the "live" item is not, so its
    // mark comes only from IsChecked as the menu opens.
    private bool _wordWrap;
    private string _theme = "system";
    private bool _clipboardEnabled = true;
    private bool _liveCheck;
    private bool _extraMenu;
    private int _revision;

    internal string Backend => settings.Gtk4 ? "GTK 4 / WebKitGTK 6" : "GTK 3 / WebKitGTK 4.1";

    public IReadOnlyList<DesktopMenu> BuildMenuBar()
    {
        lock (_gate)
        {
            var menus = new List<DesktopMenu>
            {
                new("File",
                [
                    new DesktopMenuItem("New", "file.new") { Shortcut = DesktopShortcut.Parse("Primary+N"), AccessKey = 'N' },
                    new DesktopMenuItem("Open…", "file.open") { Shortcut = DesktopShortcut.Parse("Primary+O"), AccessKey = 'O' },
                    new DesktopMenu("Open Recent",
                    [
                        new DesktopMenuItem("report-2026.odt", "file.recent") { CommandParameter = "report-2026.odt" },
                        new DesktopMenuItem("notes.md", "file.recent") { CommandParameter = "notes.md" },
                        new DesktopMenuItem("Ünïcödé ファイル.txt", "file.recent") { CommandParameter = "unicode" },
                    ]) { AccessKey = 'R' },
                    new DesktopMenuSeparator(),
                    new DesktopMenuItem("New Window", "window.new") { Shortcut = DesktopShortcut.Parse("Primary+Shift+N") },
                    new DesktopMenuItem("Close Window", "window.close") { Shortcut = DesktopShortcut.Parse("Primary+W"), AccessKey = 'C' },
                    new DesktopMenuItem("Quit", "app.quit") { Shortcut = DesktopShortcut.Parse("Primary+Q"), AccessKey = 'Q' },
                ]) { Role = DesktopMenuRole.File, AccessKey = 'F' },
                new("Edit",
                [
                    new DesktopMenuItem("Undo (declared disabled)", "edit.undo") { IsEnabled = false },
                    new DesktopMenuSeparator(),
                    new DesktopMenuItem("Cut", "edit.cut") { AccessKey = 't' },
                    new DesktopMenuItem("Copy", "edit.copy") { AccessKey = 'C' },
                    new DesktopMenuItem("Paste", "edit.paste") { AccessKey = 'P' },
                    new DesktopMenuSeparator(),
                    new DesktopMenuItem("Enable Cut/Copy/Paste", "edit.toggle-clipboard") { IsChecked = _clipboardEnabled },
                ]) { AccessKey = 'E' },
                new("View",
                [
                    new DesktopMenuItem("Word Wrap", "view.wrap") { IsChecked = _wordWrap, Shortcut = DesktopShortcut.Parse("Primary+Shift+W"), AccessKey = 'W' },
                    new DesktopMenuItem("Live Check (not rebuilt)", "view.live") { IsChecked = false },
                    new DesktopMenu("Theme",
                    [
                        Radio("Light", "light"),
                        Radio("Dark", "dark"),
                        Radio("System", "system"),
                    ]) { AccessKey = 'T' },
                    new DesktopMenu("Nested",
                    [
                        new DesktopMenu("Level 2",
                        [
                            new DesktopMenu("Level 3", [new DesktopMenuItem("Deepest item", "view.deep")]),
                        ]),
                    ]),
                    new DesktopMenu("Disabled submenu", [new DesktopMenuItem("Never runs", "view.never")]) { IsEnabled = false },
                ]) { AccessKey = 'V' },
                new($"Tools (rev {_revision})",
                [
                    new DesktopMenuItem("Rebuild Menu Bar", "tools.rebuild") { Shortcut = DesktopShortcut.Parse("Primary+Shift+R"), AccessKey = 'R' },
                    new DesktopMenuItem("Show Extra Menu", "tools.extra") { IsChecked = _extraMenu },
                    new DesktopMenuItem($"Item added in rev {_revision}", "tools.revision"),
                    new DesktopMenuItem("Größe · 日本語 · עברית · emoji 🎛", "tools.unicode"),
                    new DesktopMenuItem("A rather long menu item title to see how the global menu lays out wide entries", "tools.long"),
                    new DesktopMenuItem("Under_score & ampersand", "tools.mnemonic"),
                ]) { AccessKey = 'T' },
            };
            if (_extraMenu)
            {
                menus.Add(new DesktopMenu("Extra", [new DesktopMenuItem("Added at run time", "extra.item")]));
            }
            menus.Add(new DesktopMenu("Help", [new DesktopMenuItem("About Global Menu Check", "help.about") { AccessKey = 'A' }])
            {
                Role = DesktopMenuRole.Help,
                AccessKey = 'H',
            });
            return menus;
        }

        DesktopMenuItem Radio(string title, string value) =>
            new(title, "view.theme") { RadioGroup = "theme", CommandParameter = value, IsChecked = _theme == value };
    }

    public async Task<int> RunAsync(DesktopHost host)
    {
        _host = host;
        host.MenuCommands = this;
        Log($"Started: backend {Backend}, GlobalMenu option {settings.GlobalMenu}, frameless {settings.Frameless}, "
            + $"session {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "?"}, "
            + $"desktop {Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "?"}");
        var validation = host.Validate(WindowOptions());
        foreach (var diagnostic in validation.Diagnostics)
        {
            Log($"Validate: {diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
        }

        for (var i = 0; i < settings.InitialWindows; i++) await OpenWindowAsync();
        using var shutdown = host.ShutdownRequested.Register(() => _allClosed.TrySetResult());
        await _allClosed.Task;
        CheckWindow[] remaining;
        lock (_gate) remaining = [.. _windows];
        foreach (var window in remaining) await window.DisposeAsync();
        return 0;
    }

    private DesktopWindowOptions WindowOptions() => new()
    {
        Browser = BrowserKind.Embedded,
        Width = 980,
        Height = 760,
        Frameless = settings.Frameless,
        MenuBarPresentation = DesktopMenuBarPresentation.Native,
    };

    private async Task OpenWindowAsync()
    {
        var host = _host ?? throw new InvalidOperationException("Not started.");
        var number = Interlocked.Increment(ref _nextWindow);
        var window = await CheckWindow.OpenAsync(this, host, number, WindowOptions());
        lock (_gate) _windows.Add(window);
        Log($"Window #{number} open; placement {window.Placement}");
        PushStatus();
        _ = Task.Run(async () =>
        {
            await window.WaitForCloseAsync();
            bool last;
            lock (_gate)
            {
                _windows.Remove(window);
                last = _windows.Count == 0;
            }
            Log($"Window #{number} closed");
            await window.DisposeAsync();
            if (last) _allClosed.TrySetResult();
        });
    }

    // IDesktopMenuCommandTarget for the application (no window context). Windows forward here with their number.
    public bool CanExecute(DesktopMenuItem item) => item.CommandId switch
    {
        "edit.cut" or "edit.copy" or "edit.paste" => Volatile.Read(ref _clipboardEnabled),
        "view.never" or "edit.undo" => false,
        _ => true,
    };

    public void Execute(DesktopMenuItem item) => Execute(item, null);

    public bool? IsChecked(DesktopMenuItem item)
    {
        lock (_gate)
        {
            return item.CommandId switch
            {
                "view.wrap" => _wordWrap,
                "view.live" => _liveCheck,
                "view.theme" => item.CommandParameter == _theme,
                "edit.toggle-clipboard" => _clipboardEnabled,
                "tools.extra" => _extraMenu,
                _ => null,
            };
        }
    }

    internal void Execute(DesktopMenuItem item, CheckWindow? window)
    {
        var source = window is null ? "application" : $"window #{window.Number}";
        var parameter = item.CommandParameter is { } p ? $" [{p}]" : "";
        Log($"✔ {item.CommandId}{parameter} “{item.Title}” from {source}");
        var rebuild = false;
        lock (_gate)
        {
            switch (item.CommandId)
            {
                case "view.wrap": _wordWrap = !_wordWrap; rebuild = true; break;
                case "view.live": _liveCheck = !_liveCheck; break;
                case "view.theme": _theme = item.CommandParameter ?? "system"; rebuild = true; break;
                case "edit.toggle-clipboard": _clipboardEnabled = !_clipboardEnabled; rebuild = true; break;
                case "tools.extra": _extraMenu = !_extraMenu; rebuild = true; break;
                case "tools.rebuild": _revision++; rebuild = true; break;
            }
        }
        // Menu commands run on the UI thread; hand the work to the thread pool.
        _ = Task.Run(async () =>
        {
            try
            {
                if (rebuild) _host?.SetMenuBar(BuildMenuBar());
                switch (item.CommandId)
                {
                    case "window.new": await OpenWindowAsync(); break;
                    case "window.close" when window is not null: await window.CloseAsync(); break;
                    case "app.quit": _allClosed.TrySetResult(); break;
                }
                PushStatus();
            }
            catch (Exception error)
            {
                Log($"✘ {item.CommandId} failed: {error.GetType().Name}: {error.Message}");
            }
        });
    }

    public void OnDiagnostic(DesktopDiagnostic diagnostic) =>
        Log($"Diagnostic {diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");

    internal void OnPlacementChanged(CheckWindow window)
    {
        Log($"Window #{window.Number} placement changed → {window.Placement}");
        PushStatus();
    }

    internal void Log(string message)
    {
        var line = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}  {message}";
        Console.WriteLine(line);
        CheckWindow[] windows;
        lock (_gate)
        {
            _log.Add(line);
            if (_log.Count > 300) _log.RemoveAt(0);
            windows = [.. _windows];
        }
        foreach (var window in windows) window.Push($"gmLog({Js(line)});");
    }

    internal void PushStatus()
    {
        CheckWindow[] windows;
        lock (_gate) windows = [.. _windows];
        foreach (var window in windows) window.Push(StatusScript(window));
    }

    internal string StatusScript(CheckWindow window)
    {
        lock (_gate)
        {
            var checks = new StringBuilder();
            checks.Append("{wrap:").Append(_wordWrap ? "true" : "false")
                .Append(",live:").Append(_liveCheck ? "true" : "false")
                .Append(",clipboard:").Append(_clipboardEnabled ? "true" : "false")
                .Append(",extra:").Append(_extraMenu ? "true" : "false")
                .Append(",theme:").Append(Js(_theme))
                .Append(",revision:").Append(_revision.ToString(CultureInfo.InvariantCulture))
                .Append('}');
            return $"gmStatus({{window:{window.Number},windows:{_windows.Count},placement:{Js(window.Placement.ToString())},"
                + $"backend:{Js(Backend)},globalMenuOption:{(settings.GlobalMenu ? "true" : "false")},"
                + $"frameless:{(settings.Frameless ? "true" : "false")},"
                + $"session:{Js(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "?")},"
                + $"desktop:{Js(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "?")},state:{checks}}});";
        }
    }

    internal string LogScript()
    {
        lock (_gate)
        {
            return $"gmReplaceLog([{string.Join(",", _log.Select(Js))}]);";
        }
    }

    private static string Js(string value) => "\"" + JsonEncodedText.Encode(value).ToString() + "\"";
}

/// <summary>One window with its own surface; forwards its menu commands to the application with its number.</summary>
internal sealed class CheckWindow : IDesktopMenuCommandTarget, IAsyncDisposable
{
    private readonly MenuCheckApp _app;
    private readonly DesktopSurface _surface;
    private readonly IDisposable _connections;
    private DesktopWindow? _window;
    private int _disposed;

    private CheckWindow(MenuCheckApp app, DesktopSurface surface, int number)
    {
        _app = app;
        _surface = surface;
        Number = number;
        _connections = surface.SubscribeConnectionEvents(connection =>
        {
            if (connection.Kind != DesktopConnectionEventKind.Connected) return;
            Push(_app.StatusScript(this) + _app.LogScript());
        });
    }

    public int Number { get; }

    public DesktopMenuBarPlacement Placement => _window?.MenuBarPlacement ?? DesktopMenuBarPlacement.None;

    public static async Task<CheckWindow> OpenAsync(MenuCheckApp app, DesktopHost host, int number, DesktopWindowOptions options)
    {
        var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page.Html(number)) });
        var check = new CheckWindow(app, surface, number);
        var window = await surface.OpenWindowAsync(options);
        check._window = window;
        window.MenuCommands = check;
        window.MenuBarPlacementChanged += (_, _) => app.OnPlacementChanged(check);
        return check;
    }

    public bool CanExecute(DesktopMenuItem item) => _app.CanExecute(item);

    public void Execute(DesktopMenuItem item) => _app.Execute(item, this);

    public bool? IsChecked(DesktopMenuItem item) => _app.IsChecked(item);

    public void Push(string script)
    {
        if (Volatile.Read(ref _disposed) != 0 || _surface.ConnectionCount == 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _surface.ExecuteJavaScriptAsync(script + " return '';", timeout.Token);
            }
            catch (Exception error) when (error is InvalidOperationException or OperationCanceledException or ObjectDisposedException)
            {
                // The page left or reloaded; it gets the full state when it connects again.
            }
        });
    }

    public async Task WaitForCloseAsync()
    {
        if (_window is { } window) await window.WaitForCloseAsync();
    }

    public async Task CloseAsync()
    {
        if (_window is { } window) await window.CloseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _connections.Dispose();
        if (_window is { } window) await window.DisposeAsync();
        await _surface.DisposeAsync();
    }
}
