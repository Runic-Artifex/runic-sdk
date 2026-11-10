using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Logging.Tests;

// This suite's application in hosted mode (runic-host/1), reached through its own parameter as a guest application
// defines one: it opens its LoggingWindow in a window of the host that started it and ends when the Window closes.
internal static partial class HostedApplication
{
    internal const string Argument = "--run-hosted-application";
    internal const string FailedExitCode = "3";
    internal const string MenusArgument = "--run-hosted-menus";

    public static int Run()
    {
        var services = new ServiceCollection();
        services.AddScoped<LoggingViewModel>();
        services.AddScoped<SecondLoggingViewModel>();
        services.AddRunicViews();
        using var provider = services.BuildServiceProvider();
        try
        {
            // The test host has no web view, so a Window's open does not wait for its page.
            return RunicDesktopHost.Run(provider, new DesktopHostOptions { WaitForConnection = false },
                new RunicDesktopHostOptions
                {
                    Guest = RunicGuestOptions.FromStandardInput() with { ApplicationId = "dev.runic.tests.logging" },
                },
                async host =>
                {
                    await using var window = await host.OpenWindowAsync<LoggingWindow>();
                    await window.WaitForCloseAsync();
                    return 0;
                });
        }
        catch (RunicHostedApplicationException error)
        {
            Console.Error.WriteLine($"Hosted mode did not start ({error.Code}): {error.Message}");
            return int.Parse(FailedExitCode, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

// The same application with a menu bar (menus/1) and its window services (dialogs/1), for a guest's own tests under
// RunicTestHost: File ▸ Open… runs the window services one after the other and makes Save available; each result is a
// line on standard error.
internal static partial class HostedApplication
{
    public static int RunMenus()
    {
        var services = new ServiceCollection();
        services.AddScoped<LoggingViewModel>();
        services.AddScoped<SecondLoggingViewModel>();
        services.AddRunicViews();
        services.AddRunicPlatformServices();
        var provider = services.BuildServiceProvider();
        try
        {
            return RunicDesktopHost.Run(provider, new DesktopHostOptions { WaitForConnection = false },
                new RunicDesktopHostOptions { Guest = RunicGuestOptions.FromStandardInput() with { ApplicationId = "dev.runic.tests.logging" } },
                async host =>
                {
                    IWindowPlatformServices? platform = null;
                    var opened = false;
                    var save = new MenuCommand(() => opened, () => Console.Error.WriteLine("RAN file.save"));
                    var open = new MenuCommand(() => true, () =>
                    {
                        Console.Error.WriteLine("RAN file.open");
                        opened = true;
                        save.Changed();
                        _ = Task.Run(() => ServicesAsync(platform!));
                    });
                    host.Desktop.SetMenuBar(
                    [
                        new DesktopMenu("File",
                        [
                            new DesktopMenuItem("Open…", "file.open") { Shortcut = new DesktopShortcut("O") },
                            new DesktopMenuItem("Save", "file.save") { Shortcut = new DesktopShortcut("S") },
                        ]) { Role = DesktopMenuRole.File },
                    ]);
                    using var commands = host.UseApplicationMenu(new Dictionary<string, System.Windows.Input.ICommand>
                    {
                        ["file.open"] = open,
                        ["file.save"] = save,
                    });
                    await using var window = await host.OpenWindowAsync<LoggingWindow>();
                    platform = ((IRunicWindow)window).Services.GetRequiredService<IWindowPlatformServices>();
                    await window.WaitForCloseAsync();
                    return 0;
                });
        }
        catch (RunicHostedApplicationException error)
        {
            Console.Error.WriteLine($"Hosted mode did not start ({error.Code}): {error.Message}");
            return int.Parse(FailedExitCode, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static async Task ServicesAsync(IWindowPlatformServices platform)
    {
        switch (await platform.FileDialogs.OpenFileAsync(new OpenFileOptions { Title = "Import" }))
        {
            case PickerResult<IReadFileLease>.Selected(var lease):
                await using (lease)
                {
                    using var reader = new StreamReader(await lease.OpenReadAsync());
                    Console.Error.WriteLine("OPEN " + lease.DisplayName + " " + await reader.ReadToEndAsync());
                }
                break;
            case var other:
                Console.Error.WriteLine("OPEN " + other.GetType().Name);
                break;
        }
        var box = await platform.MessageBoxes.ShowAsync(new DesktopMessageBox("Keep the import?")
        {
            Buttons = [new DesktopMessageBoxButton("keep", "Keep") { Role = DesktopMessageBoxButtonRole.Default }, new DesktopMessageBoxButton("undo", "Undo")],
        });
        Console.Error.WriteLine("BOX " + (box is PlatformResult<DesktopMessageBoxButton?>.Success(var button) ? button?.Id ?? "none" : box.GetType().Name));
        var menu = await platform.ShowContextMenuAsync([new DesktopMenuItem("Cut", "edit.cut"), new DesktopMenuItem("Copy", "edit.copy")]);
        Console.Error.WriteLine("CONTEXT " + (menu is PlatformResult<DesktopMenuItem?>.Success(var item) ? item?.CommandId ?? "none" : menu.GetType().Name));
    }

    private sealed class MenuCommand(Func<bool> canExecute, Action execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public void Execute(object? parameter) => execute();
        public void Changed() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

// A process that poses as a guest, written without Runic's protocol code, from the spec. By default it does not know the
// offer's nonce, as one that took a gone guest's port would: it answers the host's hello with a guest proof computed
// from another nonce. With MismatchArgument it is a guest that answers protocol-mismatch with the right proof.
internal static class FakeGuest
{
    internal const string Argument = "--run-fake-guest";
    internal const string MismatchArgument = "--run-fake-guest-mismatch";

    public static int Run(bool mismatch = false)
    {
        var offer = Console.In.ReadLine();
        if (offer is null || !offer.StartsWith("RUNIC_HOST_OFFER=", StringComparison.Ordinal)) return 2;
        var nonce = Convert.FromBase64String(JsonNode.Parse(offer["RUNIC_HOST_OFFER=".Length..])!["nonce"]!.GetValue<string>()
            .Replace('-', '+').Replace('_', '/') + "=");
        var key = Base64Url(RandomNumberGenerator.GetBytes(32));
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/_runic_host/");
        listener.Start();
        Console.Out.Write("RUNIC_HOST_CHANNEL=" + new JsonObject
        {
            ["url"] = $"ws://127.0.0.1:{port}/_runic_host",
            ["key"] = key,
            ["protocol"] = 1,
        }.ToJsonString() + "\n");
        Console.Out.Flush();
        var context = listener.GetContext();
        var socket = context.AcceptWebSocketAsync(null).GetAwaiter().GetResult().WebSocket;
        var buffer = new byte[65_536];
        var received = socket.ReceiveAsync(buffer, CancellationToken.None).GetAwaiter().GetResult();
        var hello = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, received.Count))!.AsObject();
        var proofNonce = mismatch ? nonce : RandomNumberGenerator.GetBytes(32);
        var reply = new JsonObject
        {
            ["id"] = hello["id"]!.GetValue<long>(),
            ["ok"] = !mismatch,
            ["protocol"] = 1,
            ["proof"] = Base64Url(HMACSHA256.HashData(proofNonce, Encoding.ASCII.GetBytes("runic-host/1 guest " + key))),
        };
        if (mismatch)
            reply["error"] = new JsonObject { ["kind"] = "failed", ["reason"] = "protocol-mismatch", ["message"] = "Another version." };
        else
        {
            reply["guest"] = new JsonObject { ["name"] = "Fake", ["version"] = "1.0.0", ["applicationId"] = null };
            reply["features"] = new JsonArray();
        }
        socket.SendAsync(Encoding.UTF8.GetBytes(reply.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None)
            .GetAwaiter().GetResult();
        // The host closes the connection; then this process waits to be ended.
        try { socket.ReceiveAsync(buffer, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (WebSocketException) { }
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
