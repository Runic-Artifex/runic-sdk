using System.Windows.Input;

namespace Runic.Application.Views;

/// <summary>
/// A Window's root ViewModel that runs the commands of the application's menu bar while its Window is the key window.
/// </summary>
/// <remarks>
/// <para>
/// The host's menu bar names commands by id, such as <c>file.save</c> (Runic Desktop's <c>DesktopHostOptions.MenuBar</c>).
/// The host asks the root ViewModel once for each id when the Window opens, and again when the menu bar changes, in
/// the ViewModel's model turn:
/// <code>
/// public sealed partial class EditorViewModel : ObservableObject, IRunicMenuCommandSource
/// {
///     public ICommand? GetMenuCommand(string commandId) =&gt; commandId switch
///     {
///         "file.save" =&gt; SaveCommand,
///         "edit.find" =&gt; Find.OpenCommand, // a child ViewModel's command
///         _ =&gt; null,
///     };
/// }
/// </code>
/// An item follows its command's <see cref="ICommand.CanExecute"/> (and stays disabled when the menu bar declares it
/// disabled), read with the item's command parameter (<c>DesktopMenuItem.CommandParameter</c>, <see langword="null"/>
/// for none) and updated on <see cref="ICommand.CanExecuteChanged"/>, and runs <see cref="ICommand.Execute"/> with the
/// same parameter in the model turn, so one command can serve several items, such as one per recent file. An id the
/// ViewModel returns <see langword="null"/> for falls to the application's commands
/// (<c>RunicDesktopHost.UseApplicationMenu</c>), and is disabled without one.
/// </para>
/// <para>
/// Runic Desktop shows the menu bar natively, and a page can render the same bar with the window's
/// <c>MenuBarPresentation</c> set to <c>Page</c>: its items run through the same commands.
/// </para>
/// </remarks>
public interface IRunicMenuCommandSource
{
    /// <summary>Gets the command for a menu bar id, or <see langword="null"/> when this Window does not run it.</summary>
    /// <param name="commandId">The menu item's command id.</param>
    ICommand? GetMenuCommand(string commandId);
}

/// <summary>
/// A Window's root ViewModel that supplies the check marks of the menu bar's check and radio items while its Window is
/// the key window.
/// </summary>
/// <remarks>
/// <para>
/// An item declared with a non-null <c>DesktopMenuItem.IsChecked</c> is a check or radio item. The host asks
/// <see cref="IsMenuItemChecked"/> for each of them in the ViewModel's model turn when the Window opens, when the menu
/// bar changes, and in the model turn after the root ViewModel raises
/// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> for any property (once for several in a
/// turn). Only the root ViewModel is followed: when an answer depends on a child ViewModel or other state, call
/// <see cref="RunicMenu.Refresh"/> with the root ViewModel after it changed. Answer from the ViewModel's own state:
/// <code>
/// public sealed partial class EditorViewModel : ObservableObject, IRunicMenuCommandSource, IRunicMenuCheckedSource
/// {
///     [ObservableProperty] private bool _wordWrap;
///     [ObservableProperty] private int _zoom = 100;
///
///     public bool? IsMenuItemChecked(string commandId, string? commandParameter) =&gt; commandId switch
///     {
///         "view.wrap" =&gt; WordWrap,
///         "view.zoom" =&gt; commandParameter == Zoom.ToString(CultureInfo.InvariantCulture), // a radio group
///         _ =&gt; null,
///     };
/// }
/// </code>
/// The menu bar then shows the answers natively, in the page's own menu bar and in a desktop's global menu, and follows
/// the key window: each Window's root ViewModel answers for its own Window. Each item of a radio group is asked on its
/// own, so answer <see langword="true"/> for one of them. <see langword="null"/> leaves the item to the application's
/// command target and then to its declared state; an answer that throws is logged as the menu command failure and
/// counts as <see langword="null"/>. Commands that change the state are those of
/// <see cref="IRunicMenuCommandSource"/>; a ViewModel may implement this interface without it, and its items are then
/// run by the application's commands.
/// </para>
/// </remarks>
public interface IRunicMenuCheckedSource
{
    /// <summary>Gets whether a check or radio item shows its check mark, or <see langword="null"/> to keep the declaration.</summary>
    /// <param name="commandId">The menu item's command id.</param>
    /// <param name="commandParameter">The menu item's command parameter, <see langword="null"/> for none.</param>
    bool? IsMenuItemChecked(string commandId, string? commandParameter);
}

/// <summary>Asks the hosts to read the menu bar's state of Windows again.</summary>
/// <remarks>
/// The host follows only the root ViewModel's <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>
/// to read <see cref="IRunicMenuCheckedSource.IsMenuItemChecked"/> again. When a check mark depends on other state,
/// such as a child ViewModel's property or a setting outside the ViewModel, call <see cref="Refresh"/> with the root
/// ViewModel, or <see cref="RefreshAll"/>, after that state changed. Either may be called on any thread; the host reads
/// the answers in the ViewModel's next model turn, once for any number of requests before it.
/// </remarks>
public static class RunicMenu
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, List<Action>> Bindings = new();

    /// <summary>Reads the check marks of the Windows whose root ViewModel is <paramref name="viewModel"/> again.</summary>
    /// <param name="viewModel">A Window's root ViewModel that implements <see cref="IRunicMenuCheckedSource"/>; another object does nothing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> is <see langword="null"/>.</exception>
    public static void Refresh(object viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (!Bindings.TryGetValue(viewModel, out var refreshes)) return;
        Action[] snapshot;
        lock (refreshes) snapshot = [.. refreshes];
        foreach (var refresh in snapshot) refresh();
    }

    /// <summary>Reads the check marks of every open Window again.</summary>
    public static void RefreshAll()
    {
        foreach (var (_, refreshes) in Bindings)
        {
            Action[] snapshot;
            lock (refreshes) snapshot = [.. refreshes];
            foreach (var refresh in snapshot) refresh();
        }
    }

    // A Window's menu binding while it is bound (MenuCommandBinding).
    internal static void Register(object viewModel, Action refresh)
    {
        var refreshes = Bindings.GetOrCreateValue(viewModel);
        lock (refreshes) refreshes.Add(refresh);
    }

    internal static void Unregister(object viewModel, Action refresh)
    {
        if (!Bindings.TryGetValue(viewModel, out var refreshes)) return;
        lock (refreshes) refreshes.Remove(refresh);
    }
}
