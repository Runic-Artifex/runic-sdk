using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Runic.Application.Hosting;
using Runic.Application.Hosting.RefreshContract;
using Runic.Assets;
using Runic.Application.Bridge;
using Runic.Translations;

namespace Runic.Application.Hosting.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        var tests = new List<(string Name, Func<Task> Run)>
        {
            ("WebSocket transport preserves session, event, and reconnect semantics", RoundTripAndReconnectAsync),
            ("WebSocket transport rejects malformed frames and unapproved origins", RejectionAsync),
            ("WebSocket transport disposal cancels an idle uninitialized connection", DisposalAsync),
            ("authoritative asset and translation changes enter the bridge event stream", RefreshCoordinatorAsync),
            ("initialization replays current source snapshots after a disconnected change", RefreshCoordinatorReplayAsync),
            ("refresh delivery retries a saturated bridge buffer in source order", RefreshCoordinatorSaturationAsync),
            ("refresh disposal cancels in-flight delivery before completion", RefreshCoordinatorDisposalAsync),
            ("initialization completes while a full refresh queue drains", RefreshCoordinatorInitializeQueueAsync),
            ("authoritative changes reach a hosted-web bridge client", RefreshCoordinatorHostedWebAsync),
            ("compiled TypeScript client passes the hosted-web bridge contract", TypeScriptClientAsync),
            ("hosted service admission policy fixes the initial cookie and proxy boundary", HostedServiceAdmissionPolicyAsync),
            ("hosted deployment configuration requires explicit ejectable topology", HostedDeploymentConfigurationAsync),
            ("hosted service session projects only bounded identity facts", HostedServiceSessionAsync),
        };
        if (string.Equals(Environment.GetEnvironmentVariable("RUNIC_HOSTED_BROWSER_E2E"), "1", StringComparison.Ordinal))
            tests.Add(("Playwright browser smoke passes the hosted-web bridge contract", BrowserClientAsync));
        foreach ((string name, Func<Task> run) in tests)
        {
            try { await run().ConfigureAwait(false); Console.WriteLine($"ok - {name}"); }
            catch (Exception exception) { Console.Error.WriteLine($"not ok - {name}\n{exception}"); return 1; }
        }
        return 0;
    }

    private static async Task RoundTripAndReconnectAsync()
    {
        await using var session = new ApplicationBridgeSession(new TestDispatcher());
        await using var transport = new ApplicationBridgeWebSocketTransport(session);
        await using TestServer server = await TestServer.StartAsync(transport).ConfigureAwait(false);

        using var first = new ClientWebSocket();
        await first.ConnectAsync(server.WebSocketUri, CancellationToken.None).ConfigureAwait(false);
        await SendAsync(first, Initialize(Guid.Parse("00000000-0000-4000-8000-000000000001"), 0)).ConfigureAwait(false);
        JsonElement initialized = await ReceiveAsync(first).ConfigureAwait(false);
        Equal("snapshot", initialized.GetProperty("kind").GetString());
        string sessionId = initialized.GetProperty("sessionId").GetString()!;

        await SendAsync(first, Dispatch(sessionId, Guid.Parse("00000000-0000-4000-8000-000000000002"), 0)).ConfigureAwait(false);
        JsonElement receipt = await ReceiveAsync(first).ConfigureAwait(false);
        JsonElement published = await ReceiveAsync(first).ConfigureAwait(false);
        Equal("receipt", receipt.GetProperty("kind").GetString());
        Equal(2L, receipt.GetProperty("sequence").GetInt64());
        Equal("event", published.GetProperty("kind").GetString());
        Equal(3L, published.GetProperty("sequence").GetInt64());

        await SendAsync(first, Initialize(Guid.Parse("00000000-0000-4000-8000-000000000003"), 1)).ConfigureAwait(false);
        JsonElement renewed = await ReceiveAsync(first).ConfigureAwait(false);
        Equal("snapshot", renewed.GetProperty("kind").GetString());
        Equal(1L, renewed.GetProperty("connectionEpoch").GetInt64());
        Equal(1L, renewed.GetProperty("sequence").GetInt64());
        await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "reconnect", CancellationToken.None).ConfigureAwait(false);

        using var reconnected = new ClientWebSocket();
        await reconnected.ConnectAsync(server.WebSocketUri, CancellationToken.None).ConfigureAwait(false);
        await SendAsync(reconnected, Initialize(Guid.Parse("00000000-0000-4000-8000-000000000004"), 2)).ConfigureAwait(false);
        JsonElement reinitialized = await ReceiveAsync(reconnected).ConfigureAwait(false);
        Equal("snapshot", reinitialized.GetProperty("kind").GetString());
        Equal(2L, reinitialized.GetProperty("connectionEpoch").GetInt64());
        Equal(1L, reinitialized.GetProperty("sequence").GetInt64());
    }

    private static async Task RejectionAsync()
    {
        var options = new ApplicationBridgeWebSocketOptions
        {
            AllowedOrigins = new HashSet<string>(StringComparer.Ordinal) { "https://trusted.example.test" },
            Limits = new BridgeLimits { MaxFrameBytes = 1024 },
        };
        True(options.IsOriginAllowed(null));
        True(options.IsOriginAllowed("https://trusted.example.test"));
        False(options.IsOriginAllowed("https://untrusted.example.test"));

        await using var session = new ApplicationBridgeSession(new TestDispatcher());
        await using var transport = new ApplicationBridgeWebSocketTransport(session, options);
        await using TestServer server = await TestServer.StartAsync(transport).ConfigureAwait(false);
        using (var untrusted = new ClientWebSocket())
        {
            untrusted.Options.SetRequestHeader("Origin", "https://untrusted.example.test");
            bool rejected = false;
            try { await untrusted.ConnectAsync(server.WebSocketUri, CancellationToken.None).ConfigureAwait(false); }
            catch (WebSocketException) { rejected = true; }
            True(rejected);
        }
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(server.WebSocketUri, CancellationToken.None).ConfigureAwait(false);
        await SendAsync(socket, Encoding.UTF8.GetBytes("not-json")).ConfigureAwait(false);
        WebSocketReceiveResult close = await socket.ReceiveAsync(new byte[32], CancellationToken.None).ConfigureAwait(false);
        Equal(WebSocketMessageType.Close, close.MessageType);
        Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);

        using var oversized = new ClientWebSocket();
        await oversized.ConnectAsync(server.WebSocketUri, CancellationToken.None).ConfigureAwait(false);
        await SendAsync(oversized, new byte[1025]).ConfigureAwait(false);
        WebSocketReceiveResult oversizedClose = await oversized.ReceiveAsync(new byte[32], CancellationToken.None).ConfigureAwait(false);
        Equal(WebSocketMessageType.Close, oversizedClose.MessageType);
        Equal(WebSocketCloseStatus.PolicyViolation, oversized.CloseStatus);
    }

    private static async Task DisposalAsync()
    {
        await using var session = new ApplicationBridgeSession(new TestDispatcher());
        var transport = new ApplicationBridgeWebSocketTransport(session);
        await using TestServer server = await TestServer.StartAsync(transport).ConfigureAwait(false);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(server.WebSocketUri, CancellationToken.None).ConfigureAwait(false);
        await transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    private static async Task RefreshCoordinatorAsync()
    {
        await using var session = new ApplicationBridgeSession(new TestDispatcher());
        var assets = new TestAssetSourceChangeNotifier();
        var translations = new TestTranslationManager();
        await using var coordinator = CreateRefreshCoordinator(session, assets, translations);
        var received = new List<BridgeHostEnvelope>();
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.EventProduced += (_, envelope) =>
        {
            received.Add(envelope);
            if (received.Count == 2) signal.TrySetResult();
        };

        assets.Publish();
        translations.Publish("en", "de");
        await signal.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        Equal(2, received.Count);
        Equal(0L, received[0].Revision);
        Equal(0L, received[1].Revision);
        AssertFixture("asset-source-changed.event.json", received[0].Payload);
        AssertFixture("translation-locale-changed.event.json", received[1].Payload);

        await coordinator.DisposeAsync().ConfigureAwait(false);
        assets.Publish();
        await Task.Delay(50).ConfigureAwait(false);
        Equal(2, received.Count);
    }

    private static async Task RefreshCoordinatorHostedWebAsync()
    {
        await using var session = new ApplicationBridgeSession(new TestDispatcher());
        await using var transport = new ApplicationBridgeWebSocketTransport(session);
        await using TestServer server = await TestServer.StartAsync(transport).ConfigureAwait(false);
        var assets = new TestAssetSourceChangeNotifier();
        var translations = new TestTranslationManager();
        await using var coordinator = CreateRefreshCoordinator(session, assets, translations, transport);
        using var client = new ClientWebSocket();
        await client.ConnectAsync(server.WebSocketUri, CancellationToken.None).ConfigureAwait(false);
        await SendAsync(client, Initialize(Guid.Parse("00000000-0000-4000-8000-000000000005"), 0)).ConfigureAwait(false);
        JsonElement snapshot = await ReceiveAsync(client).ConfigureAwait(false);
        string sessionId = snapshot.GetProperty("sessionId").GetString()!;
        await SendAsync(client, Dispatch(sessionId, Guid.Parse("00000000-0000-4000-8000-000000000008"), 0)).ConfigureAwait(false);
        bool receipt = false;
        for (int index = 0; index < 4; index++)
        {
            JsonElement message = await ReceiveAsync(client).ConfigureAwait(false);
            if (message.GetProperty("kind").GetString() == "receipt") receipt = true;
        }
        True(receipt);

        assets.Publish();
        JsonElement assetChange = await ReceiveAsync(client).ConfigureAwait(false);
        Equal("event", assetChange.GetProperty("kind").GetString());
        AssertFixture("asset-source-changed.event.json", assetChange.GetProperty("payload"));

        translations.Publish("en", "de");
        JsonElement translationChange = await ReceiveAsync(client).ConfigureAwait(false);
        Equal("event", translationChange.GetProperty("kind").GetString());
        AssertFixture("translation-locale-changed.event.json", translationChange.GetProperty("payload"));
    }

    private static async Task RefreshCoordinatorReplayAsync()
    {
        await using var session = new ApplicationBridgeSession(new TestDispatcher());
        var assets = new TestAssetSourceChangeNotifier();
        var translations = new TestTranslationManager();
        await using var coordinator = CreateRefreshCoordinator(session, assets, translations);
        assets.Publish();
        translations.Publish("en", "de");
        await Task.Delay(50).ConfigureAwait(false);

        var replay = new List<BridgeHostEnvelope>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.EventProduced += (_, envelope) =>
        {
            replay.Add(envelope);
            if (replay.Count == 2) delivered.TrySetResult();
        };
        True(ApplicationBridgeCodec.TryDecodeClient(Initialize(Guid.Parse("00000000-0000-4000-8000-000000000006"), 0), out BridgeClientEnvelope? initialize));
        _ = await session.DispatchAsync(initialize!).ConfigureAwait(false);
        coordinator.RequestReplay();
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        AssertFixture("asset-source-changed.event.json", replay[0].Payload);
        Equal("TranslationLocaleChanged", replay[1].Payload.GetProperty("_tag").GetString());
        Equal("de", replay[1].Payload.GetProperty("oldLocale").GetString());
        Equal("de", replay[1].Payload.GetProperty("newLocale").GetString());
    }

    private static async Task RefreshCoordinatorSaturationAsync()
    {
        await using var session = new ApplicationBridgeSession(new TestDispatcher(), new BridgeLimits { MaxPendingCommands = 1 });
        var assets = new TestAssetSourceChangeNotifier();
        var translations = new TestTranslationManager();
        await using var coordinator = CreateRefreshCoordinator(session, assets, translations);
        using var release = new ManualResetEventSlim();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new List<string>();
        session.EventProduced += (_, envelope) =>
        {
            string tag = envelope.Payload.GetProperty("_tag").GetString()!;
            if (tag == "Blocker")
            {
                blocked.TrySetResult();
                release.Wait();
                return;
            }
            observed.Add(tag);
            if (observed.Count == 2) delivered.TrySetResult();
        };
        await session.PublishAsync(new(JsonDocument.Parse("""{"_tag":"Blocker"}""").RootElement.Clone())).ConfigureAwait(false);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        assets.Publish();
        translations.Publish("en", "de");
        await Task.Delay(50).ConfigureAwait(false);
        release.Set();
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Equal("AssetSourceChanged", observed[0]);
        Equal("TranslationLocaleChanged", observed[1]);
    }

    private static async Task RefreshCoordinatorDisposalAsync()
    {
        await using var session = new ApplicationBridgeSession(new TestDispatcher());
        var assets = new TestAssetSourceChangeNotifier();
        var translations = new TestTranslationManager();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool completed = false;
        var coordinator = new ApplicationBridgeRefreshCoordinator(
            session,
            assets,
            translations,
            async (_, _, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                completed = true;
            },
            static (_, _, _) => ValueTask.CompletedTask);
        assets.Publish();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        False(completed);
    }

    private static async Task RefreshCoordinatorInitializeQueueAsync()
    {
        await using var session = new ApplicationBridgeSession(new TestDispatcher());
        var assets = new TestAssetSourceChangeNotifier();
        var translations = new TestTranslationManager();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new ApplicationBridgeRefreshCoordinator(
            session,
            assets,
            translations,
            async (_, _, cancellationToken) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            },
            static (_, _, _) => ValueTask.CompletedTask);
        assets.Publish();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        for (int index = 0; index < 64; index++) translations.Publish("en", "de");
        True(ApplicationBridgeCodec.TryDecodeClient(Initialize(Guid.Parse("00000000-0000-4000-8000-000000000007"), 0), out BridgeClientEnvelope? initialize));
        BridgeHostEnvelope snapshot = await session.DispatchAsync(initialize!).AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Equal("snapshot", snapshot.Kind);
        release.TrySetResult();
    }

    private static ApplicationBridgeRefreshCoordinator CreateRefreshCoordinator(
        ApplicationBridgeSession session,
        IAssetSource assets,
        ITranslationManager translations,
        ApplicationBridgeWebSocketTransport? transport = null) =>
        new(
            session,
            assets,
            translations,
            static (publisher, change, cancellationToken) => RefreshBridgeEvents.PublishAssetSourceChangedAsync(
                publisher,
                new AssetSourceChanged
                {
                    Tag = "AssetSourceChanged",
                    ManifestVersion = change.Current.Version,
                    EntryPointPath = change.Current.EntryPoint.RelativePath,
                    ManifestFingerprint = ApplicationBridgeRefreshCoordinator.GetManifestFingerprint(change.Current),
                },
                advancesRevision: false,
                cancellationToken: cancellationToken),
            static (publisher, change, cancellationToken) => RefreshBridgeEvents.PublishTranslationLocaleChangedAsync(
                publisher,
                new TranslationLocaleChanged
                {
                    Tag = "TranslationLocaleChanged",
                    Catalog = change.NewSnapshot.Catalog,
                    OldLocale = change.OldLocale,
                    NewLocale = change.NewLocale,
                },
                advancesRevision: false,
                cancellationToken: cancellationToken),
            transport);

    private static async Task TypeScriptClientAsync()
    {
        await using var session = new ApplicationBridgeSession(new TestDispatcher());
        await using var transport = new ApplicationBridgeWebSocketTransport(session);
        await using TestServer server = await TestServer.StartAsync(transport).ConfigureAwait(false);
        string module = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "web", "packages", "application-bridge", "dist", "esm", "index.js"));
        if (!File.Exists(module)) throw new InvalidOperationException("Build @runic-artifex/application-bridge before running the hosted-web client test.");
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "hosted-web-client.mjs"));
        start.ArgumentList.Add(server.WebSocketUri.AbsoluteUri);
        start.Environment["RUNIC_APPLICATION_BRIDGE_MODULE"] = module;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Node.js hosted-web client test.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        string result = await output.ConfigureAwait(false);
        string diagnostics = await error.ConfigureAwait(false);
        if (process.ExitCode != 0 || !result.Contains("hosted-web-client-ok", StringComparison.Ordinal))
            throw new InvalidOperationException($"Hosted-web TypeScript client failed with exit code {process.ExitCode}.\n{result}\n{diagnostics}");
    }

    private static Task HostedServiceAdmissionPolicyAsync()
    {
        var suppliedProxies = new HashSet<IPAddress> { IPAddress.Parse("10.0.0.10") };
        HostedServiceAdmissionPolicy policy = HostedServiceAdmissionPolicy.CreateInitial(
            new Uri("https://app.example.test"),
            suppliedProxies);
        suppliedProxies.Clear();
        suppliedProxies.Add(IPAddress.Parse("10.0.0.99"));
        Equal("https://app.example.test/", policy.PublicOrigin.AbsoluteUri);
        True(policy.TrustedProxyAddresses.Contains(IPAddress.Parse("10.0.0.10")));
        False(policy.TrustedProxyAddresses.Contains(IPAddress.Parse("10.0.0.99")));
        Equal(1, policy.TrustedProxyAddresses.Count);
        Equal("oidc-authorization-code", HostedServiceAdmissionPolicy.AuthenticationFlow);
        Equal("__Host-runic-session", HostedServiceAdmissionPolicy.SessionCookieName);
        Equal("encrypted-host-only-cookie", HostedServiceAdmissionPolicy.SessionCarrier);
        Equal("X-Runic-CSRF", HostedServiceAdmissionPolicy.AntiforgeryHeaderName);
        Equal("/runic/service", HostedServiceAdmissionPolicy.ServiceRoutePrefix);
        Equal("/signin-oidc", HostedServiceAdmissionPolicy.OidcCallbackRoute);
        Equal("trusted-reverse-proxy", HostedServiceAdmissionPolicy.TlsTerminator);
        Equal("sveltekit-ssr", HostedServiceAdmissionPolicy.FrontendProcess);
        Equal("exact-public-origin", HostedServiceAdmissionPolicy.UnsafeRequestOriginPolicy);
        Equal("csharp", HostedServiceAdmissionPolicy.ServicePolicyOwner);
        True(HostedServiceAdmissionPolicy.FrontendMayForwardOpaqueCookieOnly);
        True(HostedServiceAdmissionPolicy.W20WebSocketRemainsLocalOnly);
        Throws<ArgumentException>(() => HostedServiceAdmissionPolicy.CreateInitial(
            new Uri("http://app.example.test"),
            new HashSet<IPAddress> { IPAddress.Parse("10.0.0.10") }));
        Throws<ArgumentException>(() => HostedServiceAdmissionPolicy.CreateInitial(
            new Uri("https://user@app.example.test"),
            new HashSet<IPAddress> { IPAddress.Parse("10.0.0.10") }));
        Throws<ArgumentException>(() => HostedServiceAdmissionPolicy.CreateInitial(
            new Uri("https://app.example.test"),
            new HashSet<IPAddress>()));
        Throws<ArgumentException>(() => HostedServiceAdmissionPolicy.CreateInitial(
            new Uri("https://app.example.test"),
            new HashSet<IPAddress> { IPAddress.Any }));
        return Task.CompletedTask;
    }

    private static Task HostedDeploymentConfigurationAsync()
    {
        var values = new Dictionary<string, string?>
        {
            ["Runic:HostedDeployment:PublicOrigin"] = "https://app.example.test",
            ["Runic:HostedDeployment:TrustedProxyAddresses"] = "10.0.0.10, 2001:db8::10",
            ["Runic:HostedDeployment:ServiceUpstream"] = "http://service.internal.test:8080",
            ["Runic:HostedDeployment:FrontendUpstream"] = "http://frontend.internal.test:3000",
            ["Runic:HostedDeployment:StaticAssetsPath"] = "frontend/build",
            ["Runic:HostedDeployment:OidcAuthority"] = "https://idp.example.test",
            ["Runic:HostedDeployment:OidcClientId"] = "runic-hosted",
            ["Runic:HostedDeployment:OidcClientSecret"] = "injected-fixture-secret",
        };
        var configuration = new ConfigurationManager();
        foreach ((string key, string? value) in values) configuration[key] = value;
        HostedDeploymentConfiguration deployment = HostedDeploymentConfiguration.Load(configuration);
        Equal("https://app.example.test/", deployment.PublicOrigin.AbsoluteUri);
        Equal("http://service.internal.test:8080/", deployment.ServiceUpstream.AbsoluteUri);
        Equal("http://frontend.internal.test:3000/", deployment.FrontendUpstream.AbsoluteUri);
        Equal("frontend/build", deployment.StaticAssetsPath);
        Equal("https://idp.example.test/", deployment.OidcAuthority.AbsoluteUri);
        Equal("runic-hosted", deployment.OidcClientId);
        Equal("injected-fixture-secret", deployment.OidcClientSecret);
        Equal(2, deployment.TrustedProxyAddresses.Count);
        True(deployment.TrustedProxyAddresses.Contains(IPAddress.Parse("10.0.0.10")));
        Equal("https://app.example.test/", deployment.CreateAdmissionPolicy().PublicOrigin.AbsoluteUri);
        Equal("sveltekit", HostedDeploymentConfiguration.StaticAssetsOwner);
        Equal("/runic/health", HostedDeploymentConfiguration.HealthPath);
        Equal("/runic/ready", HostedDeploymentConfiguration.ReadinessPath);
        Equal("runic.hosted-deployment-health/1", HostedDeploymentStatus.SchemaName);

        configuration["Runic:HostedDeployment:OidcClientSecret"] = null;
        Throws<InvalidOperationException>(() => HostedDeploymentConfiguration.Load(configuration));
        configuration["Runic:HostedDeployment:OidcClientSecret"] = "injected-fixture-secret";
        configuration["Runic:HostedDeployment:PublicOrigin"] = "http://app.example.test";
        Throws<InvalidOperationException>(() => HostedDeploymentConfiguration.Load(configuration));
        configuration["Runic:HostedDeployment:PublicOrigin"] = "https://app.example.test";
        configuration["Runic:HostedDeployment:TrustedProxyAddresses"] = "0.0.0.0";
        Throws<InvalidOperationException>(() => HostedDeploymentConfiguration.Load(configuration));
        configuration["Runic:HostedDeployment:TrustedProxyAddresses"] = "10.0.0.10";
        configuration["Runic:HostedDeployment:StaticAssetsPath"] = "../machine-assets";
        Throws<InvalidOperationException>(() => HostedDeploymentConfiguration.Load(configuration));
        configuration["Runic:HostedDeployment:StaticAssetsPath"] = "/machine-assets";
        Throws<InvalidOperationException>(() => HostedDeploymentConfiguration.Load(configuration));
        configuration["Runic:HostedDeployment:StaticAssetsPath"] = "C:\\assets";
        Throws<InvalidOperationException>(() => HostedDeploymentConfiguration.Load(configuration));
        return Task.CompletedTask;
    }

    private static Task HostedServiceSessionAsync()
    {
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "operator-1"));
        identity.AddClaim(new Claim(ClaimTypes.Name, "Operator"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "operator"));
        HostedServiceSession session = HostedServiceSession.From(new ClaimsPrincipal(identity));
        Equal("operator-1", session.Subject);
        Equal("Operator", session.DisplayName);
        Equal("operator", session.Roles.Single());
        Throws<Exception>(() => HostedServiceSession.From(new ClaimsPrincipal(new ClaimsIdentity("test"))));
        var oversized = new ClaimsIdentity("test");
        oversized.AddClaim(new Claim(ClaimTypes.NameIdentifier, new string('a', 129)));
        Throws<Exception>(() => HostedServiceSession.From(new ClaimsPrincipal(oversized)));
        return Task.CompletedTask;
    }

    private static async Task BrowserClientAsync()
    {
        string? chromium = Environment.GetEnvironmentVariable("WEBUI_BROWSER_PATH");
        if (string.IsNullOrWhiteSpace(chromium) || !File.Exists(chromium))
            throw new InvalidOperationException("WEBUI_BROWSER_PATH must name the pinned Chromium executable for the hosted browser smoke.");

        var allowedOrigins = new HashSet<string>(StringComparer.Ordinal);
        await using var session = new ApplicationBridgeSession(new TestDispatcher());
        await using var transport = new ApplicationBridgeWebSocketTransport(session, new ApplicationBridgeWebSocketOptions
        {
            AllowedOrigins = allowedOrigins,
        });
        var assets = new TestAssetSourceChangeNotifier();
        var translations = new TestTranslationManager();
        await using var coordinator = CreateRefreshCoordinator(session, assets, translations, transport);
        await using TestServer server = await TestServer.StartAsync(
            transport,
            allowedOrigins,
            () =>
            {
                assets.Publish();
                translations.Publish("en", "de");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        string script = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "tests", "Runic.Application.Hosting.Tests", "hosted-web-browser.mjs"));
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(server.HttpUri.AbsoluteUri);
        start.Environment["WEBUI_BROWSER_PATH"] = chromium;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Playwright hosted-web browser test.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        string result = await output.ConfigureAwait(false);
        string diagnostics = await error.ConfigureAwait(false);
        if (process.ExitCode != 0 || !result.Contains("hosted-web-browser-ok", StringComparison.Ordinal))
            throw new InvalidOperationException($"Hosted-web Playwright browser client failed with exit code {process.ExitCode}.\n{result}\n{diagnostics}");
    }

    private static byte[] Initialize(Guid commandId, long epoch) => Encoding.UTF8.GetBytes($$$"""
        {"protocol":"runic.test","version":1,"contractFingerprint":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","connectionEpoch":{{{epoch}}},"kind":"initialize","commandId":"{{{commandId}}}","payload":{"_tag":"InitializeApplication"}}
        """);

    private static byte[] Dispatch(string sessionId, Guid commandId, long expectedRevision) => Encoding.UTF8.GetBytes($$$"""
        {"protocol":"runic.test","version":1,"contractFingerprint":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","connectionEpoch":0,"kind":"dispatch","commandId":"{{{commandId}}}","sessionId":"{{{sessionId}}}","expectedRevision":{{{expectedRevision}}},"payload":{"_tag":"Navigate"}}
        """);

    private static async Task SendAsync(ClientWebSocket socket, byte[] frame) =>
        await socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket)
    {
        byte[] frame = new byte[1024];
        WebSocketReceiveResult result = await socket.ReceiveAsync(frame, CancellationToken.None).ConfigureAwait(false);
        Equal(WebSocketMessageType.Binary, result.MessageType);
        True(result.EndOfMessage);
        return JsonDocument.Parse(frame.AsMemory(0, result.Count)).RootElement.Clone();
    }

    private static void AssertFixture(string fileName, JsonElement actual)
    {
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", fileName)));
        True(JsonElement.DeepEquals(fixture.RootElement, actual));
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, received {actual}.");
    }

    private static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
    private static void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
    private static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}

internal sealed class TestDispatcher : IApplicationBridgeDispatcher
{
    public string ProtocolIdentity => "runic.test";
    public int ProtocolVersion => 1;
    public string ManifestFingerprint => new('a', 64);

    public async ValueTask<BridgeDispatchResult> DispatchAsync(JsonElement command, BridgeCommandContext context, CancellationToken cancellationToken)
    {
        if (command.GetProperty("_tag").GetString() == "InitializeApplication")
            return new(JsonDocument.Parse("""{"_tag":"ApplicationInitialized","snapshot":{"revision":0,"view":"Welcome"}}""").RootElement.Clone());
        await context.Events.PublishAsync(new(JsonDocument.Parse("""{"_tag":"NavigationChanged","revision":1,"view":"Complete"}""").RootElement.Clone(), AdvancesRevision: true), cancellationToken).ConfigureAwait(false);
        return new(JsonDocument.Parse("""{"_tag":"NavigationAccepted","revision":1}""").RootElement.Clone(), AdvancesRevision: true);
    }
}

internal sealed class TestAssetSourceChangeNotifier : IAssetSource, IAssetSourceChangeNotifier
{
    public event EventHandler<AssetSourceChangedEventArgs>? Changed;

    public AssetManifest Manifest { get; } = CreateManifest("app.html");

    public ValueTask ValidateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Publish() => Changed?.Invoke(this, new AssetSourceChangedEventArgs(CreateManifest("index.html"), Manifest));

    private static AssetManifest CreateManifest(string path) => new([new AssetDescriptor(path, "text/html", 1, new('a', 64), true)]);
}

internal sealed class TestTranslationManager : ITranslationManager
{
    private TestTranslationSnapshot _current = new("en");

    public string CurrentLocale => _current.Locale;
    public ITranslationSnapshot Current => _current;
    public event EventHandler<TranslationLocaleChangedEventArgs>? LocaleChanged;
    public ValueTask SetLocaleAsync(string locale, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public void Publish(string oldLocale, string newLocale)
    {
        var oldSnapshot = new TestTranslationSnapshot(oldLocale);
        var newSnapshot = new TestTranslationSnapshot(newLocale);
        _current = newSnapshot;
        LocaleChanged?.Invoke(this, new TranslationLocaleChangedEventArgs(oldSnapshot, newSnapshot));
    }
}

internal sealed class TestTranslationSnapshot(string locale) : ITranslationSnapshot
{
    public string Catalog => "test";
    public string Locale => locale;
    public bool TryGet(TranslationKey key, out string pattern) { pattern = string.Empty; return false; }
    public string Get(TranslationKey key) => throw new KeyNotFoundException();
    public string Format(TranslationKey key, ReadOnlySpan<TextArgument> arguments) => throw new KeyNotFoundException();
}

internal sealed class TestServer : IAsyncDisposable
{
    private readonly WebApplication _application;
    private TestServer(WebApplication application, Uri httpUri, Uri webSocketUri)
    {
        _application = application;
        HttpUri = httpUri;
        WebSocketUri = webSocketUri;
    }
    public Uri HttpUri { get; }
    public Uri WebSocketUri { get; }

    public static async Task<TestServer> StartAsync(
        ApplicationBridgeWebSocketTransport transport,
        ISet<string>? allowedBrowserOrigins = null,
        Func<Task>? triggerRefresh = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication application = builder.Build();
        application.UseWebSockets();
        application.MapGet("/", () => Results.Content(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "hosted-web-browser.html")), "text/html"));
        application.MapGet("/application-bridge/transport.js", () => Results.Content(
            File.ReadAllText(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "web", "packages", "application-bridge", "dist", "esm", "transport.js"))), "text/javascript"));
        if (triggerRefresh is not null)
        {
            application.MapPost("/test/refresh", async (HttpContext context) =>
            {
                await triggerRefresh().WaitAsync(context.RequestAborted).ConfigureAwait(false);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            });
        }
        application.MapRunicApplicationBridge("/bridge", transport);
        await application.StartAsync().ConfigureAwait(false);
        Uri httpUri = new(application.Urls.Single());
        var webSocketUri = new UriBuilder(httpUri) { Scheme = "ws", Path = "/bridge" }.Uri;
        allowedBrowserOrigins?.Add(httpUri.GetLeftPart(UriPartial.Authority));
        return new(application, httpUri, webSocketUri);
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }
}
