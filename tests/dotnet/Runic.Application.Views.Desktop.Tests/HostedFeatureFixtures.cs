using System.Collections.Immutable;
using System.ComponentModel;
using System.Windows.Input;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using Runic.Platform;
using Runic.Platform.Runtime;

// A dialog the host showed for a guest (FakeHostDialogs): its kind, the file dialog's request or the message box.
internal sealed record FakeHostDialog(RunicHostedDialogKind Kind, NativeFileDialogRequest? File, DesktopMessageBox? MessageBox);

// The host's dialogs for its headless windows (RunicHostedApplication.CreateDialogs): each dialog is answered by Answer,
// which returns the selected paths (null dismisses a file dialog) or a message box button id (null for none), and may
// wait for the token, which the host cancels when the window closes, the guest gives the dialog up or goes away.
internal sealed class FakeHostDialogs : INativeFilePicker, INativeDirectoryPicker, IDesktopMessageBoxes
{
    private readonly List<FakeHostDialog> _shown = [];
    private readonly List<FakeHostDialog> _cancelled = [];

    public Func<FakeHostDialog, CancellationToken, Task<object?>> Answer { get; set; } = static (_, _) => Task.FromResult<object?>(null);

    public IReadOnlyList<FakeHostDialog> Shown
    {
        get { lock (_shown) return [.. _shown]; }
    }

    // The dialogs whose token was cancelled while they were shown.
    public IReadOnlyList<FakeHostDialog> Cancelled
    {
        get { lock (_shown) return [.. _cancelled]; }
    }

    public bool SupportsMultipleSelection => true;
    public bool SupportsMultipleDirectorySelection => true;

    // Installs the dialogs for every hosted window until the result is disposed.
    public IDisposable Install()
    {
        var previous = RunicHostedApplication.CreateDialogs;
        RunicHostedApplication.CreateDialogs = _ => new HostDialogServices(this, this, null);
        return new Restore(() => RunicHostedApplication.CreateDialogs = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private async Task<object?> ShowAsync(FakeHostDialog dialog, CancellationToken cancellationToken)
    {
        lock (_shown) _shown.Add(dialog);
        try { return await Answer(dialog, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_shown) _cancelled.Add(dialog);
            throw;
        }
    }

    private static IReadOnlyList<string>? Paths(object? answer) => answer as IReadOnlyList<string>;

    public async ValueTask<IReadOnlyList<NativeFileSelection>?> SelectAsync(NativeFileDialogRequest request, CancellationToken cancellationToken)
    {
        var kind = request.Save ? RunicHostedDialogKind.SaveFile : request.AllowMultiple ? RunicHostedDialogKind.OpenFiles : RunicHostedDialogKind.OpenFile;
        return Paths(await ShowAsync(new FakeHostDialog(kind, request, null), cancellationToken)) is { } paths
            ? [.. paths.Select((path, index) => new NativeFileSelection(path, null, true) { SelectedFilter = index == 0 && !request.Filters.IsDefaultOrEmpty ? request.Filters[^1] : null })]
            : null;
    }

    public ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(string? initialDirectory, CancellationToken cancellationToken) =>
        SelectDirectoryAsync(new NativeFileDialogRequest(false, null) { InitialDirectory = initialDirectory }, cancellationToken);

    public async ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(NativeFileDialogRequest request, CancellationToken cancellationToken) =>
        Paths(await ShowAsync(new FakeHostDialog(RunicHostedDialogKind.OpenFolder, request, null), cancellationToken)) is [var path, ..]
            ? new NativeDirectorySelection(path, null)
            : null;

    public ValueTask<IReadOnlyList<NativeDirectorySelection>?> SelectDirectoriesAsync(string? initialDirectory, CancellationToken cancellationToken) =>
        SelectDirectoriesAsync(new NativeFileDialogRequest(false, null) { InitialDirectory = initialDirectory, AllowMultiple = true }, cancellationToken);

    public async ValueTask<IReadOnlyList<NativeDirectorySelection>?> SelectDirectoriesAsync(NativeFileDialogRequest request, CancellationToken cancellationToken) =>
        Paths(await ShowAsync(new FakeHostDialog(RunicHostedDialogKind.OpenFolders, request, null), cancellationToken)) is { } paths
            ? [.. paths.Select(static path => new NativeDirectorySelection(path, null))]
            : null;

    public async ValueTask<PlatformResult<DesktopMessageBoxButton?>> ShowAsync(DesktopMessageBox messageBox, CancellationToken cancellationToken = default)
    {
        var id = await ShowAsync(new FakeHostDialog(RunicHostedDialogKind.MessageBox, null, messageBox), cancellationToken) as string;
        return new PlatformResult<DesktopMessageBoxButton?>.Success(id is null || messageBox.Buttons.IsDefault ? null : messageBox.Buttons.FirstOrDefault(button => button.Id == id));
    }
}

// A Window whose root ViewModel takes part in the menu bar (IRunicMenuCommandSource, IRunicMenuCheckedSource), for the
// hosted guest's menus: Open makes Save available, Grid toggles its check mark, Deutsch declares the bar in German, Clear
// declares an empty bar. Each command that runs is reported to Ran.
internal sealed class HostedMenuWindow : RunicWindow<HostedMenuModel>;

internal sealed class HostedMenuModel : INotifyPropertyChanged, IRunicMenuCommandSource, IRunicMenuCheckedSource
{
    // Reports each command that ran: its id and parameter.
    public static Action<string, string?> Ran { get; set; } = static (_, _) => { };

    // The application's bar declares itself, such as in German.
    public static Action<bool>? Declare { get; set; }

    // The application declares an empty bar.
    public static Action? Clear { get; set; }

    private readonly HostedTestCommand _open, _save, _recent, _grid, _german, _clear;
    private bool _openRan;
    private bool _gridShown;

    public HostedMenuModel()
    {
        _save = new HostedTestCommand(() => _openRan, parameter => Ran("file.save", parameter));
        _open = new HostedTestCommand(() => true, parameter =>
        {
            Ran("file.open", parameter);
            _openRan = true;
            _save.RaiseCanExecuteChanged();
        });
        _recent = new HostedTestCommand(() => true, parameter => Ran("file.recent", parameter));
        _grid = new HostedTestCommand(() => true, parameter =>
        {
            Ran("view.grid", parameter);
            _gridShown = !_gridShown;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GridShown)));
        });
        _german = new HostedTestCommand(() => true, parameter =>
        {
            Ran("view.german", parameter);
            Declare?.Invoke(true);
        });
        _clear = new HostedTestCommand(() => true, parameter =>
        {
            Ran("view.clear", parameter);
            Clear?.Invoke();
        });
    }

    public bool GridShown => _gridShown;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand? GetMenuCommand(string commandId) => commandId switch
    {
        "file.open" => _open,
        "file.save" => _save,
        "file.recent" => _recent,
        "view.grid" => _grid,
        "view.german" => _german,
        "view.clear" => _clear,
        _ => null,
    };

    public bool? IsMenuItemChecked(string commandId, string? commandParameter) => commandId == "view.grid" ? _gridShown : null;

    // The bar of the guest application, in English or German.
    public static IReadOnlyList<DesktopMenu> Bar(bool german) =>
    [
        new DesktopMenu(german ? "Datei" : "File",
        [
            new DesktopMenuItem(german ? "Öffnen…" : "Open…", "file.open") { Shortcut = new DesktopShortcut("O") },
            new DesktopMenuItem(german ? "Sichern" : "Save", "file.save") { Shortcut = new DesktopShortcut("S") },
            new DesktopMenuSeparator(),
            new DesktopMenu(german ? "Zuletzt geöffnet" : "Recent", [new DesktopMenuItem("notes.txt", "file.recent") { CommandParameter = "notes.txt" }]),
        ]) { Role = DesktopMenuRole.File },
        new DesktopMenu(german ? "Ansicht" : "View",
        [
            new DesktopMenuItem(german ? "Raster" : "Grid", "view.grid") { IsChecked = false },
            new DesktopMenuItem("Deutsch", "view.german"),
            new DesktopMenuItem(german ? "Leeren" : "Clear", "view.clear"),
        ]),
    ];
}

internal sealed class HostedMenuBridge(IBridgeTransport transport, HostedMenuModel model)
    : ViewModelBridge<HostedMenuModel>(transport, model, "hosted-menu", [], [], [], []);

internal sealed class HostedTestCommand(Func<bool> canExecute, Action<string?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute();
    public void Execute(object? parameter) => execute(parameter as string);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

// The context menu the hosted guest shows: Cut, Copy, a separator and a disabled Paste.
internal static class HostedContextMenu
{
    public static ImmutableArray<DesktopMenuEntry> Entries =>
    [
        new DesktopMenuItem("Cut", "edit.cut"),
        new DesktopMenuItem("Copy", "edit.copy") { Shortcut = new DesktopShortcut("C") },
        new DesktopMenuSeparator(),
        new DesktopMenuItem("Paste", "edit.paste") { IsEnabled = false },
    ];
}
