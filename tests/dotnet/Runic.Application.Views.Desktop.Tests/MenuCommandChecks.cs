using System.ComponentModel;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using TUnit.Core;

// W315-002: the menu bar's command ids reach a Window's root ViewModel (IRunicMenuCommandSource) and the application's
// commands; availability follows CanExecuteChanged, and a failing command is logged.
internal sealed class MenuCommandChecks
{
    private static readonly IReadOnlyList<DesktopMenu> Bar =
    [
        new("File", [new DesktopMenuItem("New", "file.new"), new DesktopMenuItem("Save", "file.save") { Shortcut = DesktopShortcut.Parse("Primary+S") }])
        {
            Role = DesktopMenuRole.File,
        },
        new("Tools", [new DesktopMenu("Danger", [new DesktopMenuItem("Explode", "tools.explode")])]),
    ];

    [Test, NotInParallel(Order = 19)]
    public async Task WindowAndApplicationCommandsFollowTheirAvailability()
    {
        var logs = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddScoped<MenuViewModel>();
        services.AddScoped<Func<IBridgeTransport, MenuViewModel, IDisposable>>(_ => static (_, _) => new NoopAttachment());
        await using var provider = services.BuildServiceProvider();
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false, MenuBar = Bar,
        });
        var newCommand = new TestCommand();
        var host = new RunicDesktopHost(provider, desktop)
        {
            SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
            WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
            MenuCommands = new Dictionary<string, ICommand> { ["file.new"] = newCommand },
        };
        var application = desktop.MenuCommands ?? throw new InvalidOperationException("The application commands were not bound.");
        Require(application.CanExecute("file.new") && !application.CanExecute("file.save"), "The application's commands were not bound by id.");
        application.Execute("file.new");
        await newCommand.Executed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var window = await host.OpenWindowAsync<MenuWindow>();
        try
        {
            var viewModel = window.ViewModel;
            var target = ((DesktopBridgeWindow<MenuViewModel>)window.Host).Presentation.MenuCommands
                ?? throw new InvalidOperationException("The Window's root ViewModel did not become the window's menu command target.");
            Require(target.CanExecute("file.save") && target.CanExecute("tools.explode") && !target.CanExecute("file.new"),
                "The ViewModel's commands were not bound, also from a submenu.");
            viewModel.Save.Enabled = false;
            Require(!target.CanExecute("file.save"), "CanExecuteChanged did not update the item's availability.");
            viewModel.Save.Enabled = true;
            target.Execute("file.save");
            await viewModel.Save.Executed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            target.Execute("tools.explode");
            await WaitAsync(() => logs.Messages.Any(static message => message.Contains("The menu command tools.explode failed with System.InvalidOperationException", StringComparison.Ordinal)),
                "A failing menu command was not logged as DesktopMenuCommandFailed.");

            // A new menu bar binds its ids again, for example after a language change.
            desktop.SetMenuBar([new DesktopMenu("Werkzeuge", [new DesktopMenuItem("Sichern", "file.save.as")])]);
            await WaitAsync(() => target.CanExecute("file.save.as") && !target.CanExecute("file.save"), "The new menu bar was not bound.");
            await WaitAsync(() => application.CanExecute("file.new") == false, "The application's commands kept the old menu bar.");
        }
        finally
        {
            await window.DisposeAsync();
        }
    }

    private static async Task WaitAsync(Func<bool> condition, string message)
    {
        for (var waited = 0; !condition(); waited += 20)
        {
            if (waited > 5000) throw new InvalidOperationException(message);
            await Task.Delay(20);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

sealed class TestCommand(Action? execute = null) : ICommand
{
    private bool _enabled = true;
    public TaskCompletionSource Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _enabled;
    public void Execute(object? parameter)
    {
        execute?.Invoke();
        Executed.TrySetResult();
    }
}

sealed class MenuViewModel : INotifyPropertyChanged, IRunicMenuCommandSource
{
    public TestCommand Save { get; } = new();
    public TestCommand Explode { get; } = new(static () => throw new InvalidOperationException("boom"));
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public ICommand? GetMenuCommand(string commandId) => commandId switch
    {
        "file.save" or "file.save.as" => Save,
        "tools.explode" => Explode,
        _ => null,
    };
}

sealed class MenuWindow : RunicWindow<MenuViewModel>;
