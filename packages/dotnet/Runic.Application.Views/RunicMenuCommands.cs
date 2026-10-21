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
/// disabled), read with a <see langword="null"/> parameter and
/// updated on <see cref="ICommand.CanExecuteChanged"/>, and runs <see cref="ICommand.Execute"/> in the model turn. A
/// command with a required parameter cannot be a menu command. An id the ViewModel returns <see langword="null"/>
/// for falls to the application's commands (<c>RunicDesktopHost.UseApplicationMenu</c>), and is disabled without one.
/// </para>
/// <para>
/// Runic Desktop shows the menu bar on macOS. On Windows and Linux, and on CS-WebUI, there is no native menu bar in
/// this release: render one in the page, bound to the same commands through the generated client.
/// </para>
/// </remarks>
public interface IRunicMenuCommandSource
{
    /// <summary>Gets the command for a menu bar id, or <see langword="null"/> when this Window does not run it.</summary>
    /// <param name="commandId">The menu item's command id.</param>
    ICommand? GetMenuCommand(string commandId);
}
