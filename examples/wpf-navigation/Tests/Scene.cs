using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Runic.Navigation.Wpf;

namespace Runic.Navigation.Examples.Notes.Tests;

// One app instance: the app's own container and MainWindow, shown off-screen, with the notes list current.
internal sealed class Scene : IAsyncDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly ServiceProvider _services;

    private Scene(ServiceProvider services, MainWindow window)
    {
        _services = services;
        Window = window;
        Regions = services.GetRequiredService<AppRegions>();
        Store = services.GetRequiredService<NoteStore>();
        Host = (NavigationHost)window.FindName("Host");
    }

    public MainWindow Window { get; }
    public NavigationHost Host { get; }
    public AppRegions Regions { get; }
    public NoteStore Store { get; }
    public NavigationRegion<object> Main => Regions.Main;

    public static async Task<Scene> StartAsync()
    {
        var services = App.AddNotes(new ServiceCollection())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var window = new MainWindow
        {
            DataContext = services.GetRequiredService<AppRegions>(),
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        var scene = new Scene(services, window);
        Require(await scene.Main.ResetAsync<NotesListViewModel>() is NavigationResult<object>.Committed, "The list did not open.");
        await scene.PresentedAsync<NotesListViewModel>();
        return scene;
    }

    public NotesListViewModel List => (NotesListViewModel)Main.Current!;

    // The view the host presents: a new one per entry, from the naming convention.
    public FrameworkElement? View => (Host.Content as ContentPresenter)?.Content as FrameworkElement;

    public string Path => string.Join(" > ", Main.History.Select(entry => Name(entry.Content)).Append(Name(Main.Current)));

    // The Back button: NavigationCommands.BrowseBack on the host, as a click executes it.
    public bool CanClickBack => NavigationCommands.BrowseBack.CanExecute(null, Host);

    public void ClickBack()
    {
        if (CanClickBack) NavigationCommands.BrowseBack.Execute(null, Host);
    }

    public async Task<NoteDetailViewModel> OpenNoteAsync(int id)
    {
        await List.OpenCommand.ExecuteAsync(Store.Get(id));
        return await PresentedAsync<NoteDetailViewModel>();
    }

    // The ViewModel is current and the host shows a view bound to it.
    public async Task<T> PresentedAsync<T>() where T : class
    {
        await Until(() => Main.Current is T && !Main.IsTransitioning && View is { IsLoaded: true } view && view.DataContext == Main.Current,
            $"{typeof(T).Name} presented");
        return (T)Main.Current!;
    }

    // The open confirm dialog: the Dialog region's entry, shown in its own window owned by MainWindow.
    public async Task<ConfirmViewModel> ConfirmAsync()
    {
        await Until(() => Regions.Dialog.Current is ConfirmViewModel confirm && DialogWindows(confirm).Any(), "the confirm window");
        var confirm = (ConfirmViewModel)Regions.Dialog.Current!;
        Require(DialogWindows(confirm).Single().Owner == Window, "The confirm window isn't owned by the main window.");
        return confirm;
    }

    public int OpenDialogWindows => Application.Current.Windows.OfType<Window>().Count(window => window != Window && window.IsVisible);

    public async Task NoDialogAsync() =>
        await Until(() => Regions.Dialog.Current is null && OpenDialogWindows == 0, "the dialog to close");

    public async ValueTask DisposeAsync()
    {
        Window.Close();
        await _services.DisposeAsync();
    }

    // Types into the presented view's first TextBox, through its binding.
    public void Type(string text) => (Descendant<TextBox>(View!) ?? throw new InvalidOperationException("No TextBox.")).Text = text;

    public static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>([root]);
        while (queue.TryDequeue(out var node))
        {
            if (node is T match && node != root) return match;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++) queue.Enqueue(VisualTreeHelper.GetChild(node, index));
        }
        return null;
    }

    public static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
    }

    // Lets queued dispatcher work and navigation in flight run, for checks that something did NOT happen.
    public static Task Settle() => Task.Delay(200);

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IEnumerable<Window> DialogWindows(object content) =>
        Application.Current.Windows.OfType<Window>().Where(window => window.IsVisible && window.DataContext == content);

    private static string Name(object? content) => content switch
    {
        NotesListViewModel => "list",
        NoteDetailViewModel detail => $"note({detail.Title})",
        SettingsViewModel => "settings",
        null => "(empty)",
        _ => content.GetType().Name,
    };
}
