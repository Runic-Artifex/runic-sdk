using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;

// This suite's executable as a guest application in hosted mode (runic-host/1), for the host's cases
// (HostedApplicationTests): a real guest process with its pipes, its channel listener and its surfaces. The Windows'
// pages are not loaded, since the host's windows are headless. Each mode is one behaviour of the guest application.
internal static class HostedGuestProgram
{
    internal const string Argument = "--hosted-guest";
    internal const string PuppetArgument = "--puppet-guest";
    internal const string PuppetPortVariable = "RUNIC_TEST_PUPPET_PORT";
    internal const string ApplicationId = "dev.runic.tests.hosted-guest";
    internal const int StartFailedExitCode = 3;
    internal const int RefusedExitCode = 5;
    internal const int CrashExitCode = 7;

    // Starts this test executable, or dotnet and the test assembly when the suite runs through dotnet exec.
    internal static RunicGuestStartInfo StartInfo(params string[] arguments)
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("The test process has no path.");
        var all = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            all.Add(typeof(HostedGuestProgram).Assembly.Location);
        all.AddRange(arguments);
        return new RunicGuestStartInfo(path, all) { WorkingDirectory = AppContext.BaseDirectory, AllowElevatedGuest = ElevatedPair };
    }

    // RUNIC_TEST_ELEVATED_PAIR=1, in the elevated Windows CI step (ElevatedHostedApplicationTests): the host cases run as an
    // elevated host that allows its elevated guests, which allow an elevated host. Test code only: the guest's own check
    // of its parent decides, as always.
    internal const string ElevatedPairVariable = "RUNIC_TEST_ELEVATED_PAIR";

    internal static bool ElevatedPair => Environment.GetEnvironmentVariable(ElevatedPairVariable) == "1";

    internal static RunicGuestStartInfo Guest(string mode) => StartInfo(Argument, mode);

    // The page of the region-page mode, which a browser host's region shows (hosted-region-browser-smoke.mjs): a title for
    // set-title, and two fields that Tab moves through.
    private const string RegionPage = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Check</title><script src="webui.js"></script></head>
        <body><label>Name <input id="name"></label> <label>Code <input id="code"></label></body></html>
        """;

    public static int Run(string mode)
    {
        if (mode == "exit") return StartFailedExitCode;
        if (mode == "sleep")
        {
            // Reads no offer and writes no line on standard output; standard error, which the host logs, names it.
            Console.Error.WriteLine("PID " + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Console.Error.Flush();
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        if (mode == "same-user")
        {
            // The same-user check of hosted mode (host-protocol.md, Same user) against this process's real parent.
            Console.Out.WriteLine("SAME-USER " + Runic.Application.Views.Desktop.Hosting.GuestPrivileges.SameUser());
            return 0;
        }
        var services = new ServiceCollection();
        services.AddScoped<OrderedCollectionModel>();
        services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
            _ => (transport, model) => new OrderedCollectionBridge(transport, model));
        services.AddScoped<HostedMenuModel>();
        services.AddScoped<Func<IBridgeTransport, HostedMenuModel, IDisposable>>(_ => (transport, model) => new HostedMenuBridge(transport, model));
        services.AddScoped<HostedDropModel>();
        services.AddScoped<Func<IBridgeTransport, HostedDropModel, IDisposable>>(_ => (transport, model) => new HostedDropBridge(transport, model));
        // The application's locale, which follows the host's (host-protocol.md, Appearance and locale).
        services.AddRunicWindowService();
        // The Windows' IWindowPlatformServices, which hosted mode proxies to the host (dialogs/1, menus/1).
        services.AddRunicPlatformServices();
        // The platform services include ones that are only asynchronously disposable.
        var provider = services.BuildServiceProvider();
        using var disposal = new AsyncDisposal(provider);
        // The guarded Window refuses every close its host asks for; a forced close skips it.
        ConformanceGuardedWindow.Decide(mode != "guarded");
        try
        {
            return RunicDesktopHost.Run(provider, new DesktopHostOptions { WaitForConnection = false },
                new RunicDesktopHostOptions
                {
                    SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html(mode == "region-page" ? RegionPage : "<!doctype html>") },
                    // The media mode's Windows ask for the camera and microphone (window-open permissions).
                    WindowOptions = mode == "media"
                        ? RunicDesktopHost.DefaultWindowOptions with { AllowedPermissions = DesktopPermissionGrant.MediaCapture }
                        : RunicDesktopHost.DefaultWindowOptions,
                    Guest = RunicGuestOptions.FromStandardInput() with
                    {
                        ApplicationName = "Hosted guest",
                        ApplicationVersion = "1.0.0",
                        ApplicationId = ApplicationId,
                        // Every mode but "no-elevated-host" allows an elevated host: an elevated guest is then hosted only
                        // by an elevated parent of its user whose offer allows it.
                        AllowElevatedHost = mode != "no-elevated-host",
                    },
                },
                host => BodyAsync(host, mode));
        }
        catch (RunicHostedApplicationException error)
        {
            Console.Error.WriteLine($"Hosted mode did not start ({error.Code}): {error.Message}");
            return StartFailedExitCode;
        }
    }

    private static async Task<int> BodyAsync(RunicDesktopHost host, string mode)
    {
        switch (mode)
        {
            case "main":
            case "media":
            case "region-page":
            case "no-elevated-host":
            {
                await using var window = await host.OpenWindowAsync<ConformanceMainWindow>();
                await window.WaitForCloseAsync();
                return 0;
            }
            case "guarded":
            {
                await using var window = await host.OpenWindowAsync<ConformanceGuardedWindow>();
                await window.WaitForCloseAsync();
                return 0;
            }
            case "crash":
            {
                _ = await host.OpenWindowAsync<ConformanceMainWindow>();
                // Long enough for the host to see the window; then the process ends without closing it.
                await Task.Delay(TimeSpan.FromSeconds(1));
                Console.Error.WriteLine("The guest crashes.");
                Environment.Exit(CrashExitCode);
                return 0;
            }
            case "owned":
            {
                await using var main = await host.OpenWindowAsync<ConformanceMainWindow>();
                await using var owned = await host.OpenWindowAsync<ConformanceMainWindow>(
                    new RunicWindowOptions { Owner = main, Modality = RunicWindowModality.Window }, null);
                await main.WaitForCloseAsync();
                return 0;
            }
            case "flash":
            {
                // Closes its Window as soon as it opened: the host's WindowClosed follows its WindowOpened.
                var window = await host.OpenWindowAsync<ConformanceMainWindow>();
                await window.DisposeAsync();
                return 0;
            }
            case "grandchild":
            {
                // A process of its own that outlives nothing: the host's kill reaches it through the job object or the
                // process group.
                var start = StartInfo(Argument, "sleep");
                var info = new System.Diagnostics.ProcessStartInfo(start.FileName) { UseShellExecute = false, RedirectStandardError = true };
                foreach (var argument in start.Arguments) info.ArgumentList.Add(argument);
                using var child = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("The grandchild did not start.");
                Console.Error.WriteLine("GRANDCHILD " + child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await using var window = await host.OpenWindowAsync<ConformanceMainWindow>();
                await window.WaitForCloseAsync();
                return 0;
            }
            case "refused":
            {
                try
                {
                    await using var window = await host.OpenWindowAsync<ConformanceMainWindow>();
                    return 0;
                }
                catch (RunicWindowConfigurationException error)
                {
                    Console.Error.WriteLine("OPEN " + string.Join(",", error.Diagnostics.Select(static diagnostic => diagnostic.Code)));
                    return RefusedExitCode;
                }
            }
            case "present":
            {
                await using var window = await host.OpenWindowAsync<ConformanceMainWindow>();
                var presentation = ((IRunicWindow)window).Presentation;
                Report("present", await presentation.PresentAsync());
                Report("minimize", await presentation.MinimizeAsync());
                Report("visible", await presentation.SetVisibleAsync(true));
                Report("full-screen", await presentation.SetFullScreenAsync(true));
                Console.Error.WriteLine("PRESENTED");
                await window.WaitForCloseAsync();
                return 0;
            }
            case "menus":
            {
                // menus/1: the application's bar, in English until the Window's Deutsch item declares it in German, and
                // empty once its Clear item ran; each
                // command that runs is reported as RAN <id> [<parameter>].
                HostedMenuModel.Ran = static (id, parameter) => Console.Error.WriteLine("RAN " + id + (parameter is null ? "" : " " + parameter));
                HostedMenuModel.Declare = german => host.Desktop.SetMenuBar(HostedMenuModel.Bar(german));
                HostedMenuModel.Clear = () => host.Desktop.SetMenuBar([]);
                host.Desktop.SetMenuBar(HostedMenuModel.Bar(german: false));
                await using var window = await host.OpenWindowAsync<HostedMenuWindow>();
                await window.WaitForCloseAsync();
                return 0;
            }
            case "services":
            {
                // menus/1 and dialogs/1 from the Window's IWindowPlatformServices, one after the other; each result is
                // reported as a line the host's case waits for.
                await using var window = await host.OpenWindowAsync<ConformanceMainWindow>();
                await HostedServicesScript.RunAsync(((IRunicWindow)window).Services.GetRequiredService<IWindowPlatformServices>());
                await window.WaitForCloseAsync();
                return 0;
            }
            case "window-services":
            {
                // window-services/1 and the host's locale: each result is a line on standard error (HostedWindowServicesTests).
                var locale = host.Services.GetRequiredService<IRunicApplicationLocale>();
                Console.Error.WriteLine("LOCALE " + (locale.Locale ?? "none"));
                Console.Error.WriteLine("LANGUAGES " + string.Join(",", host.Guest!.Languages));
                Console.Error.WriteLine("APPEARANCE " + HostedWindowServicesScript.Describe(host.Guest.Appearance));
                locale.LocaleChanged += (_, e) => Console.Error.WriteLine("LOCALE " + (e.Locale ?? "none"));
                // Each later appearance of the host, as the application reads it.
                var session = host.Guest;
                _ = Task.Run(async () =>
                {
                    var last = session.Appearance;
                    while (!session.HostGone.IsCancellationRequested)
                    {
                        await Task.Delay(50);
                        if (Equals(session.Appearance, last)) continue;
                        last = session.Appearance;
                        Console.Error.WriteLine("APPEARANCE " + HostedWindowServicesScript.Describe(last));
                    }
                });
                await using var window = await host.OpenWindowAsync<HostedDropWindow>();
                // The script waits for the host's steps; the window may close before it ends.
                var services = ((IRunicWindow)window).Services.GetRequiredService<IWindowPlatformServices>();
                _ = Task.Run(() => HostedWindowServicesScript.RunAsync(host, locale, services));
                await window.WaitForCloseAsync();
                return 0;
            }
            case "dialog-closed":
            {
                // A message box that is open when the host's user closes the window.
                await using var window = await host.OpenWindowAsync<ConformanceMainWindow>();
                var services = ((IRunicWindow)window).Services.GetRequiredService<IWindowPlatformServices>();
                var box = services.MessageBoxes.ShowAsync(new DesktopMessageBox("Still there?"));
                await window.WaitForCloseAsync();
                Console.Error.WriteLine("BOX " + HostedServicesScript.Describe(await box));
                return 0;
            }
            case "dialog-crash":
            {
                // The guest crashes while its message box is shown.
                await using var window = await host.OpenWindowAsync<ConformanceMainWindow>();
                var services = ((IRunicWindow)window).Services.GetRequiredService<IWindowPlatformServices>();
                _ = services.MessageBoxes.ShowAsync(new DesktopMessageBox("Crash?")).AsTask();
                Console.Error.WriteLine("SHOWN");
                await Task.Delay(TimeSpan.FromSeconds(1));
                Environment.Exit(CrashExitCode);
                return 0;
            }
            default:
                throw new ArgumentException($"Unknown guest mode '{mode}'.", nameof(mode));
        }
    }

    // OUTCOME <request> success | unavailable <reason> | failed <code>, on standard error, which the host logs.
    private static void Report(string request, PlatformResult<PlatformUnit> result) => Console.Error.WriteLine("OUTCOME " + request + " " + result switch
    {
        PlatformResult<PlatformUnit>.Success => "success",
        PlatformResult<PlatformUnit>.Unavailable unavailable => "unavailable " + unavailable.Reason,
        PlatformResult<PlatformUnit>.Failed failed => "failed " + failed.Code,
        _ => "unknown",
    });

    // The host conformance runner's guest (HostRoleConformanceTests): a relay of standard input and output to the runner,
    // which plays the guest's side of the protocol over a loopback connection. A line from the runner is written to
    // standard output, except "exit <code>", which ends the process with that code.
    public static int RunPuppet()
    {
        var port = int.Parse(Environment.GetEnvironmentVariable(PuppetPortVariable)
            ?? throw new InvalidOperationException($"{PuppetPortVariable} is not set."), System.Globalization.CultureInfo.InvariantCulture);
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var input = Console.OpenStandardInput();
        new Thread(() =>
        {
            try { input.CopyTo(stream); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true }.Start();
        var output = Console.OpenStandardOutput();
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        while (reader.ReadLine() is { } command)
        {
            if (command.StartsWith("exit ", StringComparison.Ordinal))
            {
                output.Flush();
                Environment.Exit(int.Parse(command[5..], System.Globalization.CultureInfo.InvariantCulture));
            }
            output.Write(Encoding.UTF8.GetBytes(command + "\n"));
            output.Flush();
        }
        return 0;
    }
}

// The guest's window services in hosted mode (HostedGuestProgram "services"), one after the other: a context menu, file
// dialogs and message boxes that the host shows and answers (HostedMenusAndDialogsTests). Each result is a line on
// standard error, which the host logs.
internal static class HostedServicesScript
{
    public static async Task RunAsync(IWindowPlatformServices services)
    {
        Report("CONTEXT", await services.ShowContextMenuAsync(HostedContextMenu.Entries, new DesktopContextMenuOptions { X = 10, Y = 20 }) switch
        {
            PlatformResult<DesktopMenuItem?>.Success(var item) => item?.CommandId ?? "none",
            var other => Describe(other),
        });
        switch (await services.FileDialogs.OpenFileAsync(new OpenFileOptions { Title = "Import", Filters = [new FileDialogFilter("Text", ["txt"])] }))
        {
            case PickerResult<IReadFileLease>.Selected(var lease):
                await using (lease)
                {
                    using var reader = new StreamReader(await lease.OpenReadAsync());
                    Report("OPEN", lease.DisplayName + " " + await reader.ReadToEndAsync());
                }
                break;
            case var other:
                Report("OPEN", Describe(other));
                break;
        }
        switch (await services.FileDialogs.OpenFilesAsync(new OpenFileOptions()))
        {
            case PickerResult<IReadFileLeaseList>.Selected(var leases):
                await using (leases) Report("OPEN-MANY", string.Join(",", leases.Select(static lease => lease.DisplayName)));
                break;
            case var other:
                Report("OPEN-MANY", Describe(other));
                break;
        }
        switch (await services.FileDialogs.SaveDocumentAsync(new SaveFileOptions("report.txt")))
        {
            case PickerResult<IDocumentFileLease>.Selected(var lease):
                await using (lease) Report("SAVE", lease.LocalPath);
                break;
            case var other:
                Report("SAVE", Describe(other));
                break;
        }
        switch (await services.FileDialogs.OpenDirectoryAsync(new OpenDirectoryOptions()))
        {
            case PickerResult<IDirectoryLease>.Selected(var lease):
                await using (lease) Report("FOLDER", lease.LocalPath);
                break;
            case var other:
                Report("FOLDER", Describe(other));
                break;
        }
        Report("DISMISSED", Describe(await services.FileDialogs.OpenFileAsync(new OpenFileOptions { Title = "Dismiss" })));
        Report("BOX", Describe(await services.MessageBoxes.ShowAsync(new DesktopMessageBox("Save changes?")
        {
            Kind = DesktopMessageBoxKind.Warning,
            Detail = "Your changes are lost otherwise.",
            Buttons =
            [
                new DesktopMessageBoxButton("save", "Save") { Role = DesktopMessageBoxButtonRole.Default },
                new DesktopMessageBoxButton("discard", "Don't Save") { Role = DesktopMessageBoxButtonRole.Destructive },
                new DesktopMessageBoxButton("cancel", "Cancel") { Role = DesktopMessageBoxButtonRole.Cancel },
            ],
        })));
        // One dialog at a time: a file dialog while a box is shown is busy; cancelling the box closes it on the host.
        using (var hold = new CancellationTokenSource())
        {
            var held = services.MessageBoxes.ShowAsync(new DesktopMessageBox("Hold"), hold.Token).AsTask();
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Report("BUSY", Describe(await services.FileDialogs.OpenFileAsync(new OpenFileOptions())));
            await hold.CancelAsync();
            try
            {
                Report("HOLD", Describe(await held));
            }
            catch (OperationCanceledException)
            {
                Report("HOLD", "cancelled");
            }
        }
        Report("REFUSED", Describe(await services.MessageBoxes.ShowAsync(new DesktopMessageBox("Refuse"))));
        Report("DONE", "");
    }

    private static void Report(string name, string result) => Console.Error.WriteLine((name + " " + result).TrimEnd());

    internal static string Describe<T>(PlatformResult<T> result) => result switch
    {
        PlatformResult<T>.Success(var value) => value switch
        {
            DesktopMessageBoxButton button => button.Id,
            null => "none",
            _ => "success",
        },
        PlatformResult<T>.Unavailable(var reason) => "unavailable " + reason,
        PlatformResult<T>.Failed(var code, _) => "failed " + code,
        _ => "unknown",
    };

    private static string Describe<T>(PickerResult<T> result) where T : class, IAsyncDisposable => result switch
    {
        PickerResult<T>.Selected => "selected",
        PickerResult<T>.Dismissed => "dismissed",
        PickerResult<T>.Unavailable(var reason) => "unavailable " + reason,
        PickerResult<T>.Failed(var code, _) => "failed " + code,
        _ => "unknown",
    };
}

// Disposes a service provider asynchronously at the end of a synchronous Main.
internal sealed class AsyncDisposal(IAsyncDisposable disposable) : IDisposable
{
    public void Dispose() => disposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

// window-services/1 from a hosted Window (HostedGuestProgram "window-services"): after the first drop onto the window, the
// clipboard, monitors and launcher, then, once the host's locale is fr, a clipboard read and a locale of its own. Each
// result is a line on standard error, which the host's case waits for.
internal static class HostedWindowServicesScript
{
    public static async Task RunAsync(RunicDesktopHost host, IRunicApplicationLocale locale, IWindowPlatformServices services)
    {
        var dropped = await HostedDropModel.FirstDrop.Task;
        var clipboard = (IDesktopClipboard)services.Clipboard;
        Report("READ", Describe(await clipboard.ReadTextAsync(1_000)));
        Report("WRITE", Describe(await clipboard.WriteAsync(new DesktopClipboardContent { Text = "from guest", Html = "<b>from guest</b>" })));
        Report("FORMATS", Describe(await clipboard.GetFormatsAsync()));
        await foreach (var watch in clipboard.WatchAsync()) Report("CLIPBOARD-WATCH", Describe(watch));
        Report("MONITORS", Describe(await services.Monitors.ListAsync()));
        _ = Task.Run(async () =>
        {
            await foreach (var monitors in services.Monitors.WatchAsync()) Report("WATCH", Describe(monitors));
        });
        Report("LAUNCH-URI", Describe(await services.FileLauncher.LaunchUriAsync(new Uri("https://runic.dev/docs"))));
        Report("LAUNCH-SETTINGS", Describe(await services.FileLauncher.LaunchUriAsync(new Uri("ms-settings:display"))));
        Report("LAUNCH-GIVEN", Describe(await services.FileLauncher.LaunchAsync(dropped, DesktopFileOperation.Reveal)));
        Report("LAUNCH-OTHER", Describe(await services.FileLauncher.LaunchAsync(dropped + ".other")));
        Report("DRAG", Describe(await services.StartDragAsync(new DesktopDragData { Paths = [dropped] })));
        Report("ANNOUNCE", Describe(await services.AnnounceAsync("Ready")));
        Report("ANNOUNCE-ASSERTIVE", Describe(await services.AnnounceAsync("Now", PlatformAnnouncementPriority.Assertive)));
        // The guest replaces the file the user gave it with another one at the same path: no longer the user's file.
        File.WriteAllText(dropped + ".swap", "Replaced by the guest");
        File.Move(dropped + ".swap", dropped, overwrite: true);
        Report("LAUNCH-REPLACED", Describe(await services.FileLauncher.LaunchAsync(dropped, DesktopFileOperation.Reveal)));
        Report("PHASE", "1");
        // The host's locale changes to fr: the application followed it; the window is no longer active.
        while (locale.Locale != "fr") await Task.Delay(50);
        Report("READ-INACTIVE", Describe(await clipboard.ReadTextAsync(1_000)));
        Report("ANNOUNCE-INACTIVE", Describe(await services.AnnounceAsync("Later", PlatformAnnouncementPriority.Assertive)));
        // Well over a second after the user left the window: no writes either.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Report("WRITE-INACTIVE", Describe(await clipboard.WriteTextAsync("from the background")));
        await locale.SetLocaleAsync("en");
        Report("OWN", locale.Locale ?? "none");
        Report("PHASE", "2");
    }

    private static void Report(string name, string result) => Console.Error.WriteLine((name + " " + result).TrimEnd());

    internal static string Describe(DesktopAppearance? appearance) =>
        appearance is null ? "none" : $"{appearance.ColorScheme} {appearance.Contrast?.ToString() ?? "unknown"}";

    internal static string Describe<T>(PlatformResult<T> result) => result switch
    {
        PlatformResult<T>.Success(var value) => value switch
        {
            null => "none",
            string text => text,
            DesktopClipboardFormats formats => formats.ToString(),
            ImmutableArray<DesktopMonitor> monitors => string.Join(",", monitors.Select(static monitor => monitor.Id)),
            DesktopDragEffects effect => effect.ToString(),
            _ => "success",
        },
        PlatformResult<T>.Unavailable(var reason) => "unavailable " + reason,
        PlatformResult<T>.Failed(var code, _) => "failed " + code,
        _ => "unknown",
    };
}

// A Window whose root ViewModel takes dropped files: each drop is reported as DROP <paths>, and the first completes FirstDrop.
internal sealed class HostedDropWindow : RunicWindow<HostedDropModel>;

internal sealed class HostedDropModel : IRunicFileDropTarget, System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public static TaskCompletionSource<string> FirstDrop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask OnFilesDroppedAsync(RunicFileDrop drop, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("DROP " + string.Join(",", drop.Files.Select(static file => file.Path)) + (drop.Target is null ? "" : " " + drop.Target));
        FirstDrop.TrySetResult(drop.Files[0].Path);
        return ValueTask.CompletedTask;
    }
}

internal sealed class HostedDropBridge(IBridgeTransport transport, HostedDropModel model)
    : ViewModelBridge<HostedDropModel>(transport, model, "hosted-drop", [], [], [], []);
