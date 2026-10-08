namespace Runic.Navigation.Examples.Notes;

// The window's regions, in one injected service: ViewModels navigate through it and MainWindow binds to it.
public sealed class AppRegions
{
    public AppRegions(RunicNavigator navigator)
    {
        // Created empty: an initial NotesListViewModel that injects AppRegions would be a DI cycle, so App resets Main.
        Main = navigator.CreateRegion<object>(this);
        Dialog = navigator.CreateRegion<object>(this); // [S2]
    }

    public NavigationRegion<object> Main { get; }
    public NavigationRegion<object> Dialog { get; } // [S2]
}
