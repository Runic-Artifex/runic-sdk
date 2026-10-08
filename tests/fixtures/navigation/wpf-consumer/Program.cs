// A WPF app that uses only the packed Runic.Navigation.Wpf (W240-001 §11). With --smoke it shows
// its window off-screen, pushes a page, goes back with BrowseBack and exits 0.
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Runic.Navigation;
using Runic.Navigation.Wpf;

namespace WpfConsumer;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var smoke = args.Contains("--smoke");
        var app = new Application { ShutdownMode = smoke ? ShutdownMode.OnExplicitShutdown : ShutdownMode.OnMainWindowClose };
        Exception? failure = null;
        app.DispatcherUnhandledException += (_, e) =>
        {
            failure ??= e.Exception;
            e.Handled = true;
            app.Shutdown(1);
        };
        var services = new ServiceCollection()
            .AddRunicWpfNavigation(options => options.MapView<HomeViewModel, HomeView>().MapView<DetailViewModel, DetailView>())
            .AddSingleton<MainNavigation>()
            .AddSingleton<DialogNavigation>()
            .AddSingleton<ShellViewModel>()
            .AddTransient<HomeViewModel>()
            .AddTransient<DetailViewModel>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        app.Startup += async (_, _) =>
        {
            try
            {
                var shell = services.GetRequiredService<ShellViewModel>();
                var window = new MainWindow { DataContext = shell };
                if (smoke)
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = window.Top = -20000;
                    window.ShowActivated = false;
                    window.ShowInTaskbar = false;
                }
                window.Show();
                await shell.StartAsync();
                if (smoke) await SmokeAsync(shell, window);
            }
            catch (Exception error)
            {
                failure ??= error;
                app.Shutdown(1);
            }
        };
        var exit = app.Run();
        // The dispatcher has stopped and the context is closed, so disposal finishes at once.
        services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (failure is not null)
        {
            Console.Error.WriteLine(failure);
            return 1;
        }
        return exit;
    }

    private static async Task SmokeAsync(ShellViewModel shell, MainWindow window)
    {
        await WaitForAsync(() => Presented(window) is HomeView, "the home view");
        Require(await shell.Main.PushAsync<DetailViewModel>() is NavigationResult<object>.Committed, "The push did not commit.");
        await WaitForAsync(() => Presented(window) is DetailView { DataContext: DetailViewModel }, "the detail view");

        Require(NavigationCommands.BrowseBack.CanExecute(null, window.Host), "BrowseBack can't execute with history.");
        NavigationCommands.BrowseBack.Execute(null, window.Host);
        await WaitForAsync(() => shell.Main.Current is HomeViewModel && !shell.Main.IsTransitioning && Presented(window) is HomeView,
            "Back to the home view");
        Require(!shell.Main.CanGoBack, "The region still has history.");

        Console.WriteLine("WPF_NAVIGATION_CONSUMER_OK");
        window.Close();
        Application.Current.Shutdown(0);
    }

    private static object? Presented(MainWindow window) => (window.Host.Content as ContentPresenter)?.Content;

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException($"Timed out waiting for {what}.");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

// The region holder: nested ViewModels inject it instead of the shell.
public sealed class MainNavigation
{
    public MainNavigation(RunicNavigator navigator) => Region = navigator.CreateRegion<object>(this);
    public NavigationRegion<object> Region { get; }
}

public sealed class DialogNavigation
{
    public DialogNavigation(RunicNavigator navigator) => Region = navigator.CreateRegion<object>(this);
    public NavigationRegion<object> Region { get; }
}

public sealed class ShellViewModel(MainNavigation main, DialogNavigation dialogs)
{
    public NavigationRegion<object> Main => main.Region;
    public NavigationRegion<object> Dialog => dialogs.Region;
    public Task StartAsync() => Main.ResetAsync<HomeViewModel>().AsTask();
}

public sealed class HomeViewModel(MainNavigation navigation)
{
    public NavigationRegion<object> Navigation => navigation.Region;
}

public sealed class DetailViewModel;

public sealed class HomeView : UserControl
{
    public HomeView() => Content = new TextBlock { Text = "Home" };
}

public sealed class DetailView : UserControl
{
    public DetailView() => Content = new TextBlock { Text = "Detail" };
}
