using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Desktop.Tests;

// The conformance adapter of the .NET guest and host of the Runic host protocol (eng/conformance/PROTOCOL.md, "Host
// protocol"; rax.conformance-adapter/1): `bun run conformance dotnet-desktop` starts this executable with
// --conformance-adapter. For the guest role the driver plays the host: it writes the offer, connects to the channel and
// matches every message. This adapter only runs RunicDesktopHost.Run in hosted mode, in this process, with the corpus's
// test application (ConformanceMainWindow, ConformanceGuardedWindow, ConformanceDropWindow and ConformanceMenus), runs
// the test application's actions and reports their results in the README's terms. For the host role the driver plays
// the guest, and ConformanceHost (HostRoleConformanceAdapter.cs) runs the host. It checks nothing of the protocol itself.
internal static class HostConformanceAdapter
{
    public const string Argument = "--conformance-adapter";

    public static async Task<int> RunAsync()
    {
        // The protocol owns standard output; everything else the process writes goes to the log.
        var protocol = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        // The adapter mode runs instead of TUnit (Program.cs dispatches on --conformance-adapter), so no TUnit log is lost.
#pragma warning disable TUnit0055 // Overwriting the Console writer can break TUnit logging
        Console.SetOut(Console.Error);
#pragma warning restore TUnit0055
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        ConformanceGuest? guest = null;
        ConformanceHost? host = null;
        while (await input.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) continue;
            var request = JsonNode.Parse(line)!.AsObject();
            var seq = request["seq"]!.GetValue<long>();
            var op = request["op"]!.GetValue<string>();
            var args = request["args"] as JsonObject ?? [];
            JsonObject response;
            try
            {
                JsonNode? result = op switch
                {
                    "describe" => Describe(),
                    "shutdown" => new JsonObject(),
                    "host.guest.start" => Start(ref guest, args),
                    "host.guest.stop" => await StopAsync(guest, () => guest = null),
                    "host.host.start" => await StartHostAsync(host, args, started => host = started),
                    "host.host.stop" => await StopAsync(host, () => host = null),
                    _ when op.StartsWith("host.host.", StringComparison.Ordinal) => host is null
                        ? throw new InvalidOperationException($"{op} before host.host.start.")
                        : await host.HandleAsync(op, args),
                    _ when guest is null => throw new InvalidOperationException($"{op} before host.guest.start."),
                    _ => await guest.HandleAsync(op, args),
                };
                response = new JsonObject { ["seq"] = seq, ["ok"] = result };
            }
            catch (NotSupportedException error)
            {
                response = new JsonObject { ["seq"] = seq, ["error"] = new JsonObject { ["code"] = "unsupported", ["message"] = error.Message } };
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"{op}: {error}");
                response = new JsonObject { ["seq"] = seq, ["error"] = new JsonObject { ["code"] = "failed", ["message"] = $"{error.GetType().Name}: {error.Message}" } };
            }
            await protocol.WriteLineAsync(response.ToJsonString());
            if (op == "shutdown") break;
        }
        await StopAsync(guest);
        await StopAsync(host);
        return 0;
    }

    // The features are those Runic's guest lists in its hello when its application allows component embedding
    // (HostFeatures.GuestImplemented and RegionComponent), and the host's those of its hello (HostImplemented); the page
    // transport is Runic.Desktop's private profile. Runic's host fixes how long an activation after the guest's request
    // counts as the guest's (RunicHostedWindow.GuestActivationWindow), so the driver waits at least that long.
    private static JsonObject Describe() => new()
    {
        ["protocol"] = "rax.conformance-adapter/1",
        ["implementation"] = new JsonObject
        {
            ["id"] = "dotnet-desktop",
            ["name"] = "Runic.Application.Views.Desktop",
            ["version"] = typeof(RunicDesktopHost).Assembly.GetName().Version?.ToString(),
        },
        ["corpora"] = new JsonArray("host/1"),
        ["roles"] = new JsonArray("guest", "host"),
        ["features"] = new JsonArray([.. HostFeatures.GuestImplemented.Append(HostFeatures.RegionComponent).Distinct().Select(static f => (JsonNode)f)]),
        ["hostFeatures"] = new JsonArray([.. HostFeatures.HostImplemented.Select(static f => (JsonNode)f)]),
        ["guestActivationMs"] = RunicHostedWindow.GuestActivationWindow.TotalMilliseconds,
        ["capabilities"] = new JsonArray(),
        ["pageTransport"] = "webui-compat/52f9e75",
    };

    private static JsonObject Start(ref ConformanceGuest? guest, JsonObject args)
    {
        if (guest is not null) throw new InvalidOperationException("A guest is running: host.guest.stop first.");
        guest = new ConformanceGuest(args["application"]!.AsObject(), args["limits"] as JsonObject ?? []);
        return [];
    }

    private static async Task<JsonObject> StartHostAsync(ConformanceHost? host, JsonObject args, Action<ConformanceHost> started)
    {
        if (host is not null) throw new InvalidOperationException("A host is running: host.host.stop first.");
        started(await ConformanceHost.StartAsync(args["host"]!.AsObject(), args["limits"] as JsonObject ?? [], args["relayPort"]!.GetValue<int>()));
        return [];
    }

    private static async Task<JsonObject> StopAsync(IAsyncDisposable? running, Action? stopped = null)
    {
        if (running is not null) await running.DisposeAsync();
        stopped?.Invoke();
        return [];
    }
}

// One guest under test, for one scenario.
internal sealed class ConformanceGuest : IAsyncDisposable
{
    private readonly AnonymousPipeServerStream _input = new(PipeDirection.Out);
    private readonly GuestOutput _output = new();
    private readonly Dictionary<string, Task<JsonObject>> _actions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<IRunicWindow>> _windows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _openCancellations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _dialogCancellations = new(StringComparer.Ordinal);
    private readonly ConformanceMenus _menus = new();
    private readonly ConcurrentQueue<int> _exits = new();
    private readonly TaskCompletionSource<RunicDesktopHost> _host = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ServiceProvider _provider;
    private readonly Task<int> _run;
    private bool _inputClosed;
    private int _anonymous;

    public ConformanceGuest(JsonObject application, JsonObject limits)
    {
        ConformanceGuardedWindow.Reset();
        ConformanceDropModel.Reset();
        TimeSpan Limit(string name, TimeSpan fallback) => limits[name] is { } value ? TimeSpan.FromMilliseconds(value.GetValue<int>()) : fallback;
        var guest = RunicGuestOptions.FromStreams(new AnonymousPipeClientStream(PipeDirection.In, _input.ClientSafePipeHandle), _output) with
        {
            ApplicationName = application["name"]!.GetValue<string>(),
            ApplicationVersion = application["version"]!.GetValue<string>(),
            ApplicationId = application["applicationId"]?.GetValue<string>(),
            StartTimeout = Limit("startTimeout", HostProtocol.DefaultStartTimeout),
            UnauthenticatedTimeout = Limit("unauthenticatedTimeout", HostProtocol.UnauthenticatedTimeout),
            HelloTimeout = Limit("helloTimeout", HostProtocol.HelloTimeout),
            RequestTimeout = Limit("requestTimeout", HostProtocol.RequestTimeout),
            MaximumWaitingRequests = limits["waitingRequests"]?.GetValue<int>() ?? HostProtocol.MaximumWaitingRequests,
            // A guest that ends its process: recorded, so the driver sees it, instead of ending the adapter.
            ExitProcess = code => _exits.Enqueue(code),
            ReadPrivileges = static () => GuestPrivilegeFacts.StandardUser,
            AllowComponentEmbedding = application["allowComponentEmbedding"]?.GetValue<bool>() == true ? static _ => true : null,
            RunsElevated = application["elevated"]?.GetValue<bool>() == true ? static () => true : static () => false,
        };
        var componentBuild = application["componentBuild"]?.GetValue<bool>() == true;
        var services = new ServiceCollection();
        services.AddRunicPlatformServices();
        services.AddScoped<OrderedCollectionModel>();
        services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(_ => (transport, model) => new OrderedCollectionBridge(transport, model));
        services.AddScoped<ConformanceDropModel>();
        services.AddScoped<Func<IBridgeTransport, ConformanceDropModel, IDisposable>>(_ => (transport, model) => new ConformanceDropBridge(transport, model));
        services.AddRunicWindowService();
        _provider = services.BuildServiceProvider();
        var provider = _provider;
        _run = Task.Factory.StartNew(() => RunicDesktopHost.Run(provider, new DesktopHostOptions { WaitForConnection = false },
            new RunicDesktopHostOptions
            {
                SurfaceOptions = new DesktopSurfaceOptions
                {
                    Content = new DesktopContent.Html("<!doctype html>"),
                    Resources = (request, _) => ValueTask.FromResult<ContentResponse?>(!componentBuild ? null : request.Path switch
                    {
                        "/_runic_component/runic-component.json" => ContentResponse.Text(
                            """{"version":1,"name":"conformance-guest","module":"component.js","tag":"conformance-guest-00000000"}""", "application/json"),
                        "/_runic_component/component.js" => ContentResponse.Text("export const tag = \"conformance-guest-00000000\";", "text/javascript"),
                        _ => null,
                    }),
                },
                Guest = guest,
            },
            async host =>
            {
                host.Desktop.SetMenuBar(ConformanceMenus.Bar(german: false));
                using var menu = host.UseApplicationMenu(_menus.Commands);
                _host.TrySetResult(host);
                try { await Task.Delay(Timeout.Infinite, host.Desktop.ShutdownRequested); }
                catch (OperationCanceledException) { }
                return 0;
            }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public async Task<JsonNode?> HandleAsync(string op, JsonObject args)
    {
        switch (op)
        {
            case "host.guest.input":
                if (args["line"]?.GetValue<string>() is { } line)
                {
                    _input.Write(Encoding.UTF8.GetBytes(line + "\n"));
                    _input.Flush();
                }
                return new JsonObject();
            case "host.guest.closeInput":
                CloseInput();
                return new JsonObject();
            case "host.guest.line":
                return new JsonObject { ["line"] = await _output.NextAsync(Wait(args)) };
            case "host.guest.output":
                return new JsonObject { ["lines"] = new JsonArray([.. _output.Lines.Select(static l => (JsonNode)l)]) };
            case "host.guest.exit":
                return await ExitAsync(Wait(args));
            case "host.guest.act":
                await ActAsync(args["action"]!.AsObject(), args["as"]?.GetValue<string>());
                return new JsonObject();
            case "host.guest.result":
                return await ResultAsync(args["as"]!.GetValue<string>(), Wait(args));
            default:
                throw new NotSupportedException($"The .NET guest adapter has no op {op}.");
        }
    }

    private static TimeSpan Wait(JsonObject args) => TimeSpan.FromMilliseconds(args["waitMs"]?.GetValue<double>() ?? 0);

    private void CloseInput()
    {
        if (_inputClosed) return;
        _inputClosed = true;
        _input.Dispose();
    }

    // How the hosted run ended: running, returned (with Run's code) or failed (with the hosted start's code), and the
    // codes the guest tried to end its process with.
    private async Task<JsonObject> ExitAsync(TimeSpan wait)
    {
        await Task.WhenAny(_run, Task.Delay(wait));
        var exits = new JsonArray([.. _exits.Select(static code => (JsonNode)code)]);
        if (!_run.IsCompleted) return new JsonObject { ["state"] = "running", ["processExits"] = exits };
        if (_run.IsCompletedSuccessfully) return new JsonObject { ["state"] = "returned", ["code"] = _run.Result, ["processExits"] = exits };
        var error = _run.Exception!.GetBaseException();
        return new JsonObject
        {
            ["state"] = "failed",
            ["failure"] = error is RunicHostedApplicationException hosted ? hosted.Code : "unmapped:" + error.GetType().Name,
            ["message"] = error.Message,
            ["processExits"] = exits,
        };
    }

    private async Task ActAsync(JsonObject guest, string? name)
    {
        var host = await _host.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var action = guest["action"]!.GetValue<string>();
        Task<IRunicWindow> Window() => _windows[guest["window"]!.GetValue<string>()];
        Task<JsonObject> result;
        static Task<JsonObject> Done() => Task.FromResult(new JsonObject());
        switch (action)
        {
            case "open":
                var owner = guest["owner"] is { } ownerName ? await _windows[ownerName.GetValue<string>()] : null;
                var options = owner is null ? null : new RunicWindowOptions
                {
                    Owner = owner,
                    Modality = guest["modality"]?.GetValue<string>() == "window" ? RunicWindowModality.Window : RunicWindowModality.None,
                };
                var cancellation = new CancellationTokenSource();
                _openCancellations[guest["window"]!.GetValue<string>()] = cancellation;
                Task<IRunicWindow> opening = guest["type"]!.GetValue<string>() switch
                {
                    "Guarded" => Open<ConformanceGuardedWindow>(host, options, cancellation.Token),
                    "Drop" => Open<ConformanceDropWindow>(host, options, cancellation.Token),
                    _ => Open<ConformanceMainWindow>(host, options, cancellation.Token),
                };
                _windows[guest["window"]!.GetValue<string>()] = opening;
                result = Run(async () => { await opening; return new JsonObject { ["opened"] = true }; });
                break;
            case "close":
                result = Run(async () => { _ = await (await Window()).CloseAsync(TimeSpan.Zero); return new JsonObject(); });
                break;
            case "requestClose":
                result = Run(async () => new JsonObject { ["closed"] = await (await Window()).RequestCloseAsync() });
                break;
            case "present": result = Present(async window => await window.Presentation.PresentAsync()); break;
            case "minimize": result = Present(async window => await window.Presentation.MinimizeAsync()); break;
            case "setVisible": result = Present(async window => await window.Presentation.SetVisibleAsync(guest["value"]!.GetValue<bool>())); break;
            case "setFullScreen": result = Present(async window => await window.Presentation.SetFullScreenAsync(guest["value"]!.GetValue<bool>())); break;
            case "maximize": result = Present(async window => await window.Presentation.MaximizeAsync()); break;
            case "restore": result = Present(async window => await window.Presentation.RestoreAsync()); break;
            case "setDocumentEdited": result = Present(async window => await window.Presentation.SetDocumentEditedAsync(guest["value"]!.GetValue<bool>())); break;
            case "setPlacement":
                var placed = guest["value"]!.AsObject();
                var placement = new RunicWindowPlacement
                {
                    X = placed["x"]?.GetValue<int>(), Y = placed["y"]?.GetValue<int>(),
                    Width = placed["width"]!.GetValue<int>(), Height = placed["height"]!.GetValue<int>(),
                    Maximized = placed["maximized"]!.GetValue<bool>(), FullScreen = placed["fullScreen"]!.GetValue<bool>(),
                };
                result = Present(async window => await window.Presentation.SetPlacementAsync(placement));
                break;
            case "setColorScheme":
                var scheme = guest["value"]!.GetValue<string>() switch
                {
                    "light" => DesktopColorScheme.Light,
                    "dark" => DesktopColorScheme.Dark,
                    _ => DesktopColorScheme.NoPreference,
                };
                result = Present(async window => await window.Presentation.SetColorSchemeAsync(scheme));
                break;
            case "placement":
                result = Observe(async () => (await Window()).Presentation is { Placement: { } p }
                    ? new JsonObject { ["placement"] = new JsonObject { ["x"] = p.X, ["y"] = p.Y, ["width"] = p.Width, ["height"] = p.Height, ["maximized"] = p.Maximized, ["fullScreen"] = p.FullScreen } }
                    : new JsonObject { ["placement"] = null });
                break;
            case "activity":
                result = Observe(async () =>
                {
                    var activity = (await Window()).Presentation.Activity;
                    return new JsonObject { ["activity"] = new JsonObject { ["active"] = activity.IsActive, ["minimized"] = activity.IsMinimized, ["visible"] = activity.IsVisible } };
                });
                break;
            case "cancelOpen":
                await _openCancellations[guest["window"]!.GetValue<string>()].CancelAsync();
                result = Done();
                break;
            case "answerGuard":
                ConformanceGuardedWindow.Decide(guest["allow"]!.GetValue<bool>());
                result = Done();
                break;
            case "holdGuard":
                ConformanceGuardedWindow.Hold();
                result = Done();
                break;
            case "setMenuBar":
                var menu = guest["menu"]!.GetValue<string>();
                host.Desktop.SetMenuBar(menu == "none" ? [] : ConformanceMenus.Bar(german: menu == "german"));
                result = Done();
                break;
            case "setCommandEnabled":
                _menus.Command(guest["command"]!.GetValue<string>()).SetEnabled(guest["value"]!.GetValue<bool>());
                result = Done();
                break;
            case "holdCommand":
                _menus.Command(guest["command"]!.GetValue<string>()).Hold(guest["value"]!.GetValue<string>() == "blocking");
                result = Done();
                break;
            case "releaseCommand":
                _menus.Command(guest["command"]!.GetValue<string>()).Release();
                result = Done();
                break;
            case "invoked":
                result = Observe(() => Task.FromResult(new JsonObject { ["invoked"] = new JsonArray([.. _menus.Invoked.Select(static c => (JsonNode)c)]) }));
                break;
            case "contextMenu":
                var point = guest["point"] as JsonObject;
                result = Dialog(guest, async (services, token) => Normalize(await services.ShowContextMenuAsync(ConformanceMenus.ContextMenu,
                    point is null ? null : new DesktopContextMenuOptions { X = point["x"]!.GetValue<double>(), Y = point["y"]!.GetValue<double>() }, token),
                    static item => new JsonObject { ["chosen"] = item?.CommandId }));
                break;
            case "openFile":
                result = Dialog(guest, async (services, token) => await NormalizeAsync(await services.FileDialogs.OpenFileAsync(ConformanceDialogs.Open, token), static lease => [lease.DisplayName]));
                break;
            case "openFiles":
                result = Dialog(guest, async (services, token) => await NormalizeAsync(await services.FileDialogs.OpenFilesAsync(ConformanceDialogs.Open, token), static list => list.Select(static lease => lease.DisplayName)));
                break;
            case "saveFile":
                result = Dialog(guest, async (services, token) => await NormalizeAsync(await services.FileDialogs.SaveFileAsync(ConformanceDialogs.Save, token), static lease => [lease.DisplayName]));
                break;
            case "openFolder":
                result = Dialog(guest, async (services, token) => await NormalizeAsync(await services.FileDialogs.OpenDirectoryAsync(ConformanceDialogs.Folder, token), static lease => [lease.DisplayName]));
                break;
            case "messageBox":
                result = Dialog(guest, async (services, token) => Normalize(await services.MessageBoxes.ShowAsync(ConformanceDialogs.MessageBox, token),
                    static button => new JsonObject { ["button"] = button?.Id }));
                break;
            case "locale":
                result = Observe(() => Task.FromResult(new JsonObject { ["locale"] = host.Services.GetRequiredService<IRunicApplicationLocale>().Locale }));
                break;
            case "setLocale":
                var own = guest["value"]?.GetValue<string>();
                result = Run(async () => { await host.Services.GetRequiredService<IRunicApplicationLocale>().SetLocaleAsync(own); return new JsonObject(); });
                break;
            case "appearance":
                result = Observe(async () =>
                {
                    var content = (await Window()) switch
                    {
                        RunicWindow<OrderedCollectionModel> main => main.Content,
                        RunicWindow<ConformanceDropModel> drop => drop.Content,
                        _ => null,
                    };
                    if (content?.Appearance is not { } appearance) return new JsonObject { ["appearance"] = null };
                    var push = JsonNode.Parse(BridgeDispatcher.AppearancePush(appearance, content.DecidedAppearance))!.AsObject();
                    push.Remove("kind");
                    return new JsonObject { ["appearance"] = push };
                });
                break;
            case "readClipboard":
                result = Run(async () => Normalize(await (await Window()).Services.GetRequiredService<IWindowPlatformServices>().Clipboard.ReadTextAsync(1_024),
                    static text => new JsonObject { ["text"] = text }));
                break;
            case "writeClipboard":
                var copied = guest["value"]!.GetValue<string>();
                result = Present(async window => await window.Services.GetRequiredService<IWindowPlatformServices>().Clipboard.WriteTextAsync(copied));
                break;
            case "monitors":
                result = Run(async () => Normalize(await (await Window()).Services.GetRequiredService<IWindowPlatformServices>().Monitors.ListAsync(),
                    static monitors => new JsonObject { ["monitors"] = new JsonArray([.. monitors.Select(static m => (JsonNode)m.Id)]) }));
                break;
            case "launch":
                var target = guest["value"]!.GetValue<string>();
                result = Present(async window =>
                {
                    var launcher = window.Services.GetRequiredService<IWindowPlatformServices>().FileLauncher;
                    return Path.IsPathFullyQualified(target)
                        ? await launcher.LaunchAsync(target, DesktopFileOperation.Reveal)
                        : await launcher.LaunchUriAsync(new Uri(target, UriKind.Absolute));
                });
                break;
            case "drops":
                result = Observe(() => Task.FromResult(new JsonObject { ["dropped"] = new JsonArray([.. ConformanceDropModel.Dropped.Select(static n => (JsonNode)n)]) }));
                break;
            case "setTitle":
            {
                // The Window's page is not loaded: its title reaches the guest as the page would report it.
                var title = guest["value"]!.GetValue<string>();
                var region = host.HostedRegionOf(await Window()) ?? throw new InvalidOperationException("The Window is not in a region.");
                region.Report(new Runic.Desktop.Internal.DesktopFrameReport(title, null));
                result = Done();
                break;
            }
            case "cancelDialog":
                if (_dialogCancellations.Remove(guest["window"]!.GetValue<string>(), out var dialogCancellation))
                {
                    await dialogCancellation.CancelAsync();
                    dialogCancellation.Dispose();
                }
                result = Done();
                break;
            default:
                throw new NotSupportedException($"The test application has no action {action}.");
        }
        _actions[name ?? $"#{++_anonymous}"] = result;

        Task<JsonObject> Present(Func<IRunicWindow, Task<PlatformResult<PlatformUnit>>> call) =>
            Run(async () => new JsonObject { ["outcome"] = Outcome(await call(await Window())) });
    }

    // An observed state: the action's result is read again each time the driver asks (host.guest.result).
    private readonly Dictionary<Task<JsonObject>, Func<Task<JsonObject>>> _observers = [];

    private Task<JsonObject> Observe(Func<Task<JsonObject>> read)
    {
        var first = Run(read);
        _observers[first] = read;
        return first;
    }

    private Task<JsonObject> Dialog(JsonObject guest, Func<IWindowPlatformServices, CancellationToken, Task<JsonObject>> call)
    {
        var windowName = guest["window"]!.GetValue<string>();
        if (!_dialogCancellations.TryGetValue(windowName, out var cancellation))
            _dialogCancellations[windowName] = cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var window = _windows[windowName];
        return Run(async () => await call((await window).Services.GetRequiredService<IWindowPlatformServices>(), token));
    }

    private async Task<JsonObject> ResultAsync(string name, TimeSpan wait)
    {
        if (!_actions.TryGetValue(name, out var action)) throw new InvalidOperationException($"No action {name}.");
        await Task.WhenAny(action, Task.Delay(wait));
        if (!action.IsCompleted) return new JsonObject { ["state"] = "pending" };
        if (action.IsCanceled) return new JsonObject { ["state"] = "cancelled" };
        if (action.Exception?.GetBaseException() is { } error)
            return error switch
            {
                OperationCanceledException => new JsonObject { ["state"] = "cancelled" },
                RunicWindowConfigurationException configuration => new JsonObject
                {
                    ["state"] = "failed",
                    ["codes"] = new JsonArray([.. configuration.Diagnostics.Select(static d => (JsonNode)d.Code)]),
                    ["message"] = error.Message,
                },
                _ => new JsonObject { ["state"] = "faulted", ["message"] = $"{error.GetType().Name}: {error.Message}" },
            };
        var value = _observers.TryGetValue(action, out var read) ? await read() : action.Result;
        return new JsonObject { ["state"] = "done", ["value"] = value.DeepClone() };
    }

    private static Task<JsonObject> Run(Func<Task<JsonObject>> work) => Task.Run(work);

    private static async Task<IRunicWindow> Open<TWindow>(RunicDesktopHost host, RunicWindowOptions? options, CancellationToken cancellationToken)
        where TWindow : class, IRunicWindow, new() =>
        await host.OpenWindowAsync<TWindow>(options, null, cancellationToken);

    // A platform outcome in the README's terms: success, unavailable with its reason or failed with its code, in kebab case.
    private static JsonObject Outcome<T>(PlatformResult<T> result) => result switch
    {
        PlatformResult<T>.Success => new JsonObject { ["success"] = new JsonObject() },
        PlatformResult<T>.Unavailable(var reason) => new JsonObject { ["unavailable"] = HostProtocol.Kebab(reason.ToString()) },
        PlatformResult<T>.Failed(var code, _) => new JsonObject { ["failed"] = HostProtocol.Kebab(code.ToString()) },
        _ => throw new InvalidOperationException($"Unknown result {result}."),
    };

    private static JsonObject Normalize<T>(PlatformResult<T> result, Func<T, JsonObject> success) =>
        result is PlatformResult<T>.Success(var value) ? success(value) : new JsonObject { ["outcome"] = Outcome(result) };

    private static async Task<JsonObject> NormalizeAsync<T>(PickerResult<T> result, Func<T, IEnumerable<string>> names) where T : IAsyncDisposable
    {
        switch (result)
        {
            case PickerResult<T>.Selected(var value):
                try { return new JsonObject { ["selected"] = new JsonArray([.. names(value).Select(static name => (JsonNode)name)]) }; }
                finally { await value.DisposeAsync(); }
            case PickerResult<T>.Dismissed:
                return new JsonObject { ["dismissed"] = true };
            case PickerResult<T>.Unavailable(var reason):
                return new JsonObject { ["outcome"] = new JsonObject { ["unavailable"] = HostProtocol.Kebab(reason.ToString()) } };
            case PickerResult<T>.Failed(var code, _):
                return new JsonObject { ["outcome"] = new JsonObject { ["failed"] = HostProtocol.Kebab(code.ToString()) } };
            default:
                throw new InvalidOperationException($"Unknown result {result}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        ConformanceGuardedWindow.Reset();
        CloseInput();
        try { await _run.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (Exception error) when (error is RunicHostedApplicationException or TimeoutException) { }
        catch (Exception error) { Console.Error.WriteLine($"The hosted run ended with {error.GetType().Name}: {error.Message}"); }
        await _provider.DisposeAsync();
        foreach (var cancellation in _openCancellations.Values) cancellation.Dispose();
        foreach (var cancellation in _dialogCancellations.Values) cancellation.Dispose();
        _output.Dispose();
    }

    // The guest's standard output, line by line.
    private sealed class GuestOutput : TextWriter
    {
        private readonly List<string> _all = [];
        private readonly StringBuilder _line = new();
        private readonly System.Threading.Channels.Channel<string> _lines = System.Threading.Channels.Channel.CreateUnbounded<string>();

        public override Encoding Encoding => Encoding.UTF8;

        public IReadOnlyList<string> Lines
        {
            get { lock (_all) return [.. _all]; }
        }

        public override void Write(char value)
        {
            lock (_all)
            {
                if (value != '\n')
                {
                    _line.Append(value);
                    return;
                }
                var line = _line.ToString().TrimEnd('\r');
                _line.Clear();
                _all.Add(line);
                _lines.Writer.TryWrite(line);
            }
        }

        // The next line, or null when none arrived within the wait.
        public async Task<string?> NextAsync(TimeSpan wait)
        {
            using var timeout = new CancellationTokenSource(wait);
            try { return await _lines.Reader.ReadAsync(timeout.Token); }
            catch (OperationCanceledException) { return null; }
        }
    }
}
