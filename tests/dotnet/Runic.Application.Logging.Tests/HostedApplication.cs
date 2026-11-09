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

namespace Runic.Application.Logging.Tests;

// This suite's application in hosted mode (runic-host/1), reached through its own parameter as a guest application
// defines one: it opens its LoggingWindow in a window of the host that started it and ends when the Window closes.
internal static class HostedApplication
{
    internal const string Argument = "--run-hosted-application";
    internal const string FailedExitCode = "3";

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

// A process that poses as a guest without the offer's nonce, as one that took a gone guest's port would: it answers the
// host's hello with a guest proof computed from another nonce. Written without Runic's protocol code, from the spec.
internal static class FakeGuest
{
    internal const string Argument = "--run-fake-guest";

    public static int Run()
    {
        var offer = Console.In.ReadLine();
        if (offer is null || !offer.StartsWith("RUNIC_HOST_OFFER=", StringComparison.Ordinal)) return 2;
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
        var otherNonce = RandomNumberGenerator.GetBytes(32);
        var reply = new JsonObject
        {
            ["id"] = hello["id"]!.GetValue<long>(),
            ["ok"] = true,
            ["protocol"] = 1,
            ["proof"] = Base64Url(HMACSHA256.HashData(otherNonce, Encoding.ASCII.GetBytes("runic-host/1 guest " + key))),
            ["guest"] = new JsonObject { ["name"] = "Fake", ["version"] = "1.0.0", ["applicationId"] = null },
            ["features"] = new JsonArray(),
        };
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
