using Runic.Desktop;
using Runic.Desktop.Gtk4;
using Runic.Platform;
using Runic.Platform.Linux.Gtk4;
using Runic.Platform.Runtime;
using System.Runtime.Versioning;

#pragma warning disable CA1416 // The executable checks Linux before entering the annotated smoke path.

using var watchdog = new Timer(static _ =>
{
    Console.Error.WriteLine("GTK 4 smoke exceeded its 60-second deadline.");
    Environment.Exit(1);
}, null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
SmokeMode.PortalParentOnly = args.Contains("--portal-parent-only", StringComparer.Ordinal);

try
{
    if (!OperatingSystem.IsLinux())
    {
        throw new PlatformNotSupportedException("The GTK 4 native smoke runs on Linux only.");
    }
    AssertRunnerContract();
    Environment.ExitCode = RunSmoke();
    Console.WriteLine("PASS GTK 4 / WebKitGTK 6 native window lifecycle and capability contract.");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}

[SupportedOSPlatform("linux")]
static void AssertRunnerContract()
{
    var factory = new Gtk4WindowHostFactory();
    try
    {
        _ = factory.Create();
        throw new InvalidOperationException("GTK4 factory accepted host creation outside Gtk4Application.Run.");
    }
    catch (InvalidOperationException error) when (error.Message.Contains("Gtk4Application.Run", StringComparison.Ordinal))
    {
    }

    var rejectedWorker = Task.Run(() =>
    {
        try
        {
            _ = Gtk4Application.Run(static () => Task.FromResult(0));
            return false;
        }
        catch (InvalidOperationException error)
        {
            return error.Message.Contains("process main thread", StringComparison.Ordinal);
        }
    }).GetAwaiter().GetResult();
    if (!rejectedWorker)
    {
        throw new InvalidOperationException("GTK4 runner accepted a worker-thread initialization.");
    }
}

[SupportedOSPlatform("linux")]
static int RunSmoke() => Gtk4Application.Run(async () =>
{
    await RunAsync();
    return 0;
});

[SupportedOSPlatform("linux")]
static async Task RunAsync()
{
    var factory = new Gtk4WindowHostFactory();
    if (!factory.IsSupported)
    {
        throw new PlatformNotSupportedException("GTK 4 and WebKitGTK 6 are not available for the native smoke.");
    }
    if (factory.Capabilities.HasFlag(DesktopWindowCapabilities.Move))
    {
        throw new InvalidOperationException("GTK 4 must not advertise unsupported global window movement.");
    }

    await using var host = factory.Create();
    Console.WriteLine("GTK4 smoke: opening WebKit window.");
    await host.OpenAsync(new Uri("about:blank"), new DesktopWindowHostOptions
    {
        Width = 640,
        Height = 480,
        Hidden = false,
    });
    if (!host.IsOpen || host.NativeHandle == 0)
    {
        throw new InvalidOperationException("GTK 4 host did not create a native window.");
    }
    if (host.Capabilities.HasFlag(DesktopWindowCapabilities.Move))
    {
        throw new InvalidOperationException("GTK 4 host advertised unsupported global window movement.");
    }

    var owner = new HostOwner((IDesktopNativeDispatchWindowHost)host);
    await using (var portalParent = await Gtk4PlatformProvider.CreatePortalWindowOwner(owner).ExportParentAsync())
    {
        Console.WriteLine($"GTK4 smoke: exported {portalParent.Identifier.Split(':')[0]} portal parent.");
        if (!portalParent.Identifier.StartsWith("x11:", StringComparison.Ordinal) &&
            !portalParent.Identifier.StartsWith("wayland:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("GTK 4 did not export an X11 or Wayland portal parent.");
        }
    }
    if (SmokeMode.PortalParentOnly)
    {
        await host.CloseAsync();
        return;
    }
    var clipboard = Gtk4PlatformProvider.CreateTextClipboard(owner);
    try
    {
        if (await clipboard.WriteTextAsync("") is not PlatformResult<Unit>.Success ||
            await clipboard.ReadTextAsync(0) is not PlatformResult<string?>.Success { Value: "" })
        {
            throw new InvalidOperationException("GTK 4 empty clipboard text was not preserved.");
        }
        if (await clipboard.WriteTextAsync("Runic GTK4 clipboard") is not PlatformResult<Unit>.Success ||
            await clipboard.ReadTextAsync(64) is not PlatformResult<string?>.Success { Value: "Runic GTK4 clipboard" })
        {
            throw new InvalidOperationException("GTK 4 clipboard round-trip failed.");
        }
        if (await clipboard.ReadTextAsync(3) is not PlatformResult<string?>.Failed { Code: FailureCode.TooLarge })
        {
            throw new InvalidOperationException("GTK 4 clipboard character bound was not enforced.");
        }
        var successor = Gtk4PlatformProvider.CreateTextClipboard(owner);
        try
        {
            _ = await successor.WriteTextAsync("successor");
            await ((IAsyncDisposable)clipboard).DisposeAsync();
            if (await successor.ReadTextAsync(64) is not PlatformResult<string?>.Success { Value: "successor" })
            {
                throw new InvalidOperationException("GTK 4 disposal cleared another writer's clipboard content.");
            }
        }
        finally { await ((IAsyncDisposable)successor).DisposeAsync(); }
    }
    finally
    {
        if (clipboard is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }

    Console.WriteLine("GTK4 smoke: exercising lifecycle operations.");
    await host.ResizeAsync(700, 500);
    await host.FocusAsync();
    await host.ToggleMaximizedAsync();
    await host.SetVisibleAsync(false);
    await host.NavigateAsync(new Uri("about:blank#reloaded"));
    Console.WriteLine("GTK4 smoke: closing window.");
    await host.CloseAsync();
    if (host.IsOpen || host.NativeHandle != 0)
    {
        throw new InvalidOperationException("GTK 4 host did not release its native window.");
    }

    await ExerciseDesktopHostAsync(factory);
}

[SupportedOSPlatform("linux")]
static async Task ExerciseDesktopHostAsync(Gtk4WindowHostFactory factory)
{
    Console.WriteLine("GTK4 smoke: exercising explicit DesktopHost integration and bridge.");
    await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
    {
        Linux = new LinuxDesktopOptions { EmbeddedBackend = LinuxEmbeddedBackend.Gtk4WebKit6 },
        WindowHostFactory = factory,
        WaitForConnection = true,
    });
    await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
    {
        Content = "<!doctype html><title>GTK4 bridge</title><script src=\"webui.js\"></script><main>GTK4</main>",
    });

    var decisions = new Queue<bool>([false, true]);
    await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
    {
        Browser = BrowserKind.Embedded,
        Hidden = true,
        ConfirmCloseAsync = _ => ValueTask.FromResult(decisions.Dequeue()),
    });
    if (window.Capabilities.HasFlag(DesktopWindowCapabilities.Move) ||
        await surface.ExecuteJavaScriptAsync("return document.title;", TimeSpan.FromSeconds(10)) != "GTK4 bridge")
    {
        throw new InvalidOperationException("GTK 4 DesktopHost bridge or capability contract failed.");
    }
    if (await window.RequestCloseAsync() || !window.IsOpen)
    {
        throw new InvalidOperationException("GTK 4 close-veto did not keep the bridge window open.");
    }
    if (!await window.RequestCloseAsync() || window.IsOpen)
    {
        throw new InvalidOperationException("GTK 4 close retry did not close the bridge window.");
    }

    await using var restartedSurface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
    {
        Content = "<!doctype html><title>GTK4 restarted</title><script src=\"webui.js\"></script>",
    });
    await using var restartedWindow = await restartedSurface.OpenWindowAsync(new DesktopWindowOptions
    {
        Browser = BrowserKind.Embedded,
        Hidden = true,
    });
    if (await restartedSurface.ExecuteJavaScriptAsync("return document.title;", TimeSpan.FromSeconds(10)) != "GTK4 restarted")
    {
        throw new InvalidOperationException("GTK 4 did not reopen a DesktopHost window on the same application runner.");
    }
    await restartedWindow.CloseAsync();
}

internal sealed class HostOwner(IDesktopNativeDispatchWindowHost host) : INativePickerOwner
{
    public Guid Generation { get; } = Guid.NewGuid();
    public bool IsAvailable => host.IsOpen && host.NativeHandle != 0;

    public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) =>
        host.DispatchNativeAsync(() =>
        {
            if (!IsAvailable)
            {
                throw new OwnerClosedException();
            }
            action(host.NativeHandle);
        }, cancellationToken);
}

internal static class SmokeMode
{
    internal static bool PortalParentOnly { get; set; }
}
#pragma warning restore CA1416
