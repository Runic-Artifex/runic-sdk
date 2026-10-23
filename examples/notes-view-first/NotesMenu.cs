using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Runic.Platform;

namespace NotesWindowViews;

// The window's menu bar. A CS-WebUI window has no native bar, so each frontend renders this one with its menubar
// library (README, "In-page menu bar"); on Runic Desktop the same declaration is DesktopHostOptions.MenuBar.
public sealed class NotesMenuBar
{
    private DocumentPane _pane = DocumentPane.Editor;
    private bool _sidebar = true;

    /// <summary>The bar for the current View choices.</summary>
    public IReadOnlyList<DesktopMenu> Current => Declare(_pane, _sidebar);

    /// <summary>Raised with the new bar after a View choice; the application passes it to <c>SetMenuBar</c>.</summary>
    public event Action<IReadOnlyList<DesktopMenu>>? Changed;

    /// <summary>Receives each menu command that ran, as <c>commandId</c> or <c>commandId:parameter</c>.</summary>
    public static Action<string>? Trace { get; set; }

    internal void Choose(DocumentPane? pane = null, bool? sidebar = null)
    {
        _pane = pane ?? _pane;
        _sidebar = sidebar ?? _sidebar;
        Changed?.Invoke(Current);
    }

    internal bool Sidebar => _sidebar;

    // Edit is declared before File: Windows and Linux put File first.
    private static IReadOnlyList<DesktopMenu> Declare(DocumentPane pane, bool sidebar) =>
    [
        new("Edit", [
            new DesktopMenuItem("Undo", "edit.undo") { AccessKey = 'U', Shortcut = new DesktopShortcut("Z") },
            new DesktopMenuItem("Find", "edit.find") { AccessKey = 'F', Shortcut = new DesktopShortcut("F") },
        ]) { AccessKey = 'E' },
        new("File", [
            new DesktopMenuItem("New", "file.new") { AccessKey = 'N', Shortcut = new DesktopShortcut("N") },
            new DesktopMenuItem("Save", "file.save") { AccessKey = 'S', Shortcut = new DesktopShortcut("S") },
            new DesktopMenuSeparator(),
            new DesktopMenu("Export", [new DesktopMenuItem("PDF", "file.export") { AccessKey = 'P', CommandParameter = "pdf" }]) { AccessKey = 'E' },
        ]) { Role = DesktopMenuRole.File, AccessKey = 'F' },
        new("View", [
            new DesktopMenuItem("Editor", "view.pane")
                { AccessKey = 'E', RadioGroup = "view.pane", CommandParameter = "editor", IsChecked = pane == DocumentPane.Editor },
            new DesktopMenuItem("Preview", "view.pane")
                { AccessKey = 'P', RadioGroup = "view.pane", CommandParameter = "preview", IsChecked = pane == DocumentPane.Preview },
            new DesktopMenuSeparator(),
            new DesktopMenuItem("Sidebar", "view.sidebar") { AccessKey = 'S', IsChecked = sidebar },
        ]) { AccessKey = 'V' },
        new("Help", [new DesktopMenuItem("About Notes", "help.about") { AccessKey = 'A' }]) { Role = DesktopMenuRole.Help, AccessKey = 'H' },
    ];
}

// The commands of the bar, which the window's ShellViewModel returns by id. Undo has nothing to undo, so its
// CanExecute keeps it disabled; the View items change the bar's checked state.
public sealed partial class NotesMenuCommands(NotesMenuBar bar)
{
    private readonly Dictionary<string, ICommand> _reports = new(StringComparer.Ordinal);

    public ICommand? Get(string commandId) => commandId switch
    {
        "file.new" or "file.save" or "file.export" or "edit.find" or "help.about" => Report(commandId),
        "edit.undo" => UndoCommand,
        "view.pane" => PaneCommand,
        "view.sidebar" => SidebarCommand,
        _ => null,
    };

    // The items this example only reports.
    private ICommand Report(string commandId)
    {
        lock (_reports)
        {
            if (!_reports.TryGetValue(commandId, out var command))
                _reports[commandId] = command = new RelayCommand<string>(parameter =>
                    NotesMenuBar.Trace?.Invoke(parameter is null ? commandId : $"{commandId}:{parameter}"));
            return command;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => NotesMenuBar.Trace?.Invoke("edit.undo");

    private static bool CanUndo() => false;

    [RelayCommand]
    private void Pane(string? pane)
    {
        NotesMenuBar.Trace?.Invoke($"view.pane:{pane}");
        bar.Choose(pane: pane == "preview" ? DocumentPane.Preview : DocumentPane.Editor);
    }

    [RelayCommand]
    private void Sidebar()
    {
        NotesMenuBar.Trace?.Invoke("view.sidebar");
        bar.Choose(sidebar: !bar.Sidebar);
    }
}
