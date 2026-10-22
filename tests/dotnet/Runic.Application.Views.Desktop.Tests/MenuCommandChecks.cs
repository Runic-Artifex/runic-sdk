using System.ComponentModel;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;
using TUnit.Core;

// W315-002: the menu bar's command ids reach a Window's root ViewModel (IRunicMenuCommandSource) and the application's
// commands; availability follows CanExecuteChanged, and a failing command is logged.
internal sealed class MenuCommandChecks
{
    // The command target receives the item; the binding routes it by CommandId.
    private static DesktopMenuItem Item(string commandId) => new(commandId, commandId);

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
        var host = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
        {
            SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
            WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
        });
        using var menu = host.UseApplicationMenu(new Dictionary<string, ICommand> { ["file.new"] = newCommand });
        var application = desktop.MenuCommands ?? throw new InvalidOperationException("The application commands were not bound.");
        Require(application.CanExecute(Item("file.new")) && !application.CanExecute(Item("file.save")), "The application's commands were not bound by id.");
        application.Execute(Item("file.new"));
        await newCommand.Executed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var window = await host.OpenWindowAsync<MenuWindow>();
        try
        {
            var viewModel = window.ViewModel;
            var target = ((DesktopBridgeWindow<MenuViewModel>)window.Host).Window.MenuCommands
                ?? throw new InvalidOperationException("The Window's root ViewModel did not become the window's menu command target.");
            Require(target.CanExecute(Item("file.save")) && target.CanExecute(Item("tools.explode")) && !target.CanExecute(Item("file.new")),
                "The ViewModel's commands were not bound, also from a submenu.");
            viewModel.Save.Enabled = false;
            Require(!target.CanExecute(Item("file.save")), "CanExecuteChanged did not update the item's availability.");
            viewModel.Save.Enabled = true;
            target.Execute(Item("file.save"));
            await viewModel.Save.Executed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            target.Execute(Item("tools.explode"));
            await WaitAsync(() => logs.Messages.Any(static message => message.Contains("The menu command tools.explode failed with System.InvalidOperationException", StringComparison.Ordinal)),
                "A failing menu command was not logged as DesktopMenuCommandFailed.");

            // A new menu bar binds its ids again, for example after a language change.
            desktop.SetMenuBar([new DesktopMenu("Werkzeuge", [new DesktopMenuItem("Sichern", "file.save.as")])]);
            await WaitAsync(() => target.CanExecute(Item("file.save.as")) && !target.CanExecute(Item("file.save")), "The new menu bar was not bound.");
            await WaitAsync(() => application.CanExecute(Item("file.new")) == false, "The application's commands kept the old menu bar.");
        }
        finally
        {
            await window.DisposeAsync();
        }
    }

    // B10: an item's CommandParameter is the command's parameter, for CanExecute and Execute, and availability is
    // cached per command id and parameter.
    [Test, NotInParallel(Order = 21)]
    public async Task ItemsPassTheirParameterToTheCommand()
    {
        IReadOnlyList<DesktopMenu> bar =
        [
            new("View",
            [
                new DesktopMenuItem("Small", "view.zoom") { RadioGroup = "zoom", IsChecked = false, CommandParameter = "0.8" },
                new DesktopMenuItem("Large", "view.zoom") { RadioGroup = "zoom", IsChecked = true, CommandParameter = "1.5" },
                new DesktopMenuItem("Huge", "view.zoom") { RadioGroup = "zoom", IsChecked = false, CommandParameter = "4" },
            ]),
        ];
        await using var provider = new ServiceCollection().BuildServiceProvider();
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false, MenuBar = bar,
        });
        var zoom = new ParameterCommand(static parameter => parameter is "0.8" or "1.5");
        using var menu = new RunicDesktopHost(provider, desktop).UseApplicationMenu(new Dictionary<string, ICommand> { ["view.zoom"] = zoom });
        var target = desktop.MenuCommands ?? throw new InvalidOperationException("The application commands were not bound.");
        var items = bar[0].Items.Cast<DesktopMenuItem>().ToArray();
        Require(target.CanExecute(items[0]) && target.CanExecute(items[1]) && !target.CanExecute(items[2]),
            "CanExecute was not asked with each item's parameter.");
        Require(!target.CanExecute(new DesktopMenuItem("Other", "view.zoom") { CommandParameter = "2" }),
            "A parameter no item of the bar passes was reported runnable.");
        target.Execute(items[1]);
        await WaitAsync(() => zoom.Executed.Contains("1.5"), "Execute did not receive the item's parameter.");
        target.Execute(items[2]);
        await Task.Delay(50);
        Require(!zoom.Executed.Contains("4"), "An item whose command cannot run with its parameter ran.");
        zoom.Allow = static parameter => parameter is "4";
        await WaitAsync(() => target.CanExecute(items[2]) && !target.CanExecute(items[0]), "CanExecuteChanged did not refresh each parameter.");
    }

    // vdesk-01: application commands are application-wide and released. A second host on the same Desktop host cannot
    // silently replace them, and disposing the result unbinds them and their CanExecuteChanged subscriptions.
    [Test, NotInParallel(Order = 20)]
    public async Task ApplicationMenuIsOnePerDesktopHostAndReleased()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false, MenuBar = Bar,
        });
        var first = new RunicDesktopHost(provider, desktop);
        var second = new RunicDesktopHost(provider, desktop);
        var newCommand = new TestCommand();
        var menu = first.UseApplicationMenu(new Dictionary<string, ICommand> { ["file.new"] = newCommand });
        var bound = desktop.MenuCommands ?? throw new InvalidOperationException("The application commands were not bound.");
        Require(newCommand.Subscribers == 1 && bound.CanExecute(Item("file.new")), "The commands were not bound when UseApplicationMenu returned.");

        var replacement = new TestCommand();
        try
        {
            second.UseApplicationMenu(new Dictionary<string, ICommand> { ["file.new"] = replacement }).Dispose();
            throw new InvalidOperationException("A second application menu on the same Desktop host did not throw.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("already has application menu commands", StringComparison.Ordinal)) { }
        Require(ReferenceEquals(desktop.MenuCommands, bound) && replacement.Subscribers == 0,
            "A second host replaced the application's commands or kept its own subscriptions.");

        menu.Dispose();
        Require(desktop.MenuCommands is null && newCommand.Subscribers == 0 && !bound.CanExecute(Item("file.new")),
            "Disposing the application menu kept its commands or their CanExecuteChanged subscriptions.");
        menu.Dispose();
        using var again = second.UseApplicationMenu(new Dictionary<string, ICommand> { ["file.new"] = newCommand });
        Require(desktop.MenuCommands is not null && newCommand.Subscribers == 1, "A released application menu could not be replaced.");
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
        set { _enabled = value; _canExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
    private EventHandler? _canExecuteChanged;
    public int Subscribers => _canExecuteChanged?.GetInvocationList().Length ?? 0;
    public event EventHandler? CanExecuteChanged
    {
        add => _canExecuteChanged += value;
        remove => _canExecuteChanged -= value;
    }
    public bool CanExecute(object? parameter) => _enabled;
    public void Execute(object? parameter)
    {
        execute?.Invoke();
        Executed.TrySetResult();
    }
}

sealed class ParameterCommand(Func<object?, bool> allow) : ICommand
{
    private Func<object?, bool> _allow = allow;
    public System.Collections.Concurrent.ConcurrentBag<object?> Executed { get; } = [];
    public Func<object?, bool> Allow
    {
        get => _allow;
        set { _allow = value; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _allow(parameter);
    public void Execute(object? parameter) => Executed.Add(parameter);
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
