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
