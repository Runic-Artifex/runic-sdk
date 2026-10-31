using TUnit.Core;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Runic.Desktop.Tests;

public sealed class DesktopApiTests
{
    private const int PacketHeaderSize = 8;

    // D26 (W319-003): native-thread work uses the CheckAccess/InvokeAsync vocabulary of Runic.Platform and WPF.
    [Test]
    public void NativeDispatchUsesTheInvokeVocabulary()
    {
        Assert.Null(typeof(DesktopWindow).GetMethod("DispatchNativeAsync"));
        Assert.Equal(typeof(Action<nint>), typeof(DesktopWindow).GetMethod(nameof(DesktopWindow.InvokeNativeAsync))!.GetParameters()[0].ParameterType);
        var spi = typeof(IDesktopNativeDispatchWindowHost).GetMethod(nameof(IDesktopNativeDispatchWindowHost.InvokeNativeAsync))!;
        Assert.Null(typeof(IDesktopNativeDispatchWindowHost).GetMethod("DispatchNativeAsync"));
        Assert.True(spi.GetParameters()[1].HasDefaultValue, "The SPI defaults its token like the window member.");
    }

    [Test]
    [DisplayName("MacOsDiscoveryRequiresFrameworkAndAnAvailableMainThread(frameworkAvailable: $frameworkAvailable, isMainThread: $isMainThread, eventLoopRunning: $eventLoopRunning, diagnosticCode: $diagnosticCode)")]
    [Arguments(true, false, true, null)] // Application.Run worker, AppKit loop active.
    [Arguments(true, false, false, "macos-main-thread-required")]
    [Arguments(true, true, false, null)] // Direct synchronous main-thread startup.
    [Arguments(true, true, true, null)]
    [Arguments(false, false, true, "webkit-runtime-missing")]
    [Arguments(false, true, false, "webkit-runtime-missing")]
    public void MacOsDiscoveryRequiresFrameworkAndAnAvailableMainThread(
        bool frameworkAvailable, bool isMainThread, bool eventLoopRunning, string? diagnosticCode)
    {
        var diagnostic = DesktopPlatform.GetMacOsEmbeddedDiagnostic(frameworkAvailable, isMainThread, eventLoopRunning);
        Assert.Equal(diagnosticCode, diagnostic?.Code);
        if (diagnosticCode == "macos-main-thread-required")
            Assert.Contains("DesktopEventLoop.Run()", diagnostic!.Remediation, StringComparison.Ordinal);
    }

    [Test]
    public async Task LinuxEmbeddedSelectionIsRequiredAndActionable()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var host = await DesktopHost.StartAsync();
        var availability = host.GetPresentationPreflight(new() { Browser = BrowserKind.Embedded });
        Assert.False(availability.IsAvailable);
        Assert.Equal("linux-embedded-backend-not-selected", availability.Diagnostics[0].Code);
        await using var surface = await host.CreateSurfaceAsync();
        var error = await Assert.ThrowsAsync<DesktopException>(async () => await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded }));
        Assert.Equal("linux-embedded-backend-not-selected", error.Code);
    }

    [Test]
    public async Task Gtk4SelectionRequiresItsOptionalProvider()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var host = await DesktopHost.StartAsync(new() { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk4WebKit6 } });
        Assert.Equal("gtk4-provider-missing", host.GetPresentationPreflight(new() { Browser = BrowserKind.Embedded }).Diagnostics[0].Code);
    }

    [Test]
    public async Task LinuxFactoryMustMatchTheDeclaredBackend()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await DesktopHost.StartAsync(new()
        {
            Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 },
            WindowHostFactory = new TaggedLinuxFactory(),
        }));
    }

    private sealed class TaggedLinuxFactory : ILinuxDesktopWindowHostFactory
    {
        public LinuxEmbeddedBackend Backend => LinuxEmbeddedBackend.Gtk4WebKit6;
        public bool IsSupported => false;
        public IDesktopWindowHost Create() => throw new NotSupportedException();
    }

    [Test]
    public void StructuredPayloadRejectsDuplicateKeysWithStableRedactedError()
    {
        var error = Assert.Throws<DesktopException>(() => StructuredPayload.Parse(
            "example.message/1",
            "{\"name\":\"first\",\"name\":\"second\"}"u8));

        Assert.Equal(DesktopErrorCategory.InvalidFrame, error.Category);
        Assert.Equal("duplicate-object-key", error.Code);
        Assert.Equal("The structured payload contains a duplicate object key.", error.Message);
        Assert.False(error.Retryable);
    }

    [Test]
    public void PublicApiContainsNoWebUiCompatibilityIdentity()
    {
        // The full surface is tracked by PublicAPI.Shipped.txt and PublicApiAnalyzers.
        Assert.DoesNotContain(
            typeof(DesktopHost).Assembly.ExportedTypes,
            static type => type.FullName!.Contains("WebUi", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task PublicNetworkExposureRequiresAnExplicitSecurityPolicy()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await DesktopHost.StartAsync(new DesktopHostOptions
            {
                NetworkExposure = DesktopNetworkExposure.AllInterfaces,
            }));

        Assert.Contains("explicit security policy", error.Message, StringComparison.Ordinal);

        var copied = new DesktopHostOptions { Port = 1 } with { NetworkExposure = DesktopNetworkExposure.AllInterfaces };
        await Assert.ThrowsAsync<ArgumentException>(async () => await DesktopHost.StartAsync(copied));
        await using var chosen = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            NetworkExposure = DesktopNetworkExposure.AllInterfaces,
            Security = DesktopSecurityPolicy.Default,
        });
    }

    [Test]
    public async Task PublicSurfaceServesRunicDesktopBootstrapIdentity()
    {
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync();
        using var client = new HttpClient();

        var script = await client.GetStringAsync(new Uri(surface.Url, "runic-desktop.js"));

        Assert.Contains("globalThis, \"runicDesktop\"", script, StringComparison.Ordinal);
        Assert.Contains("product: \"Runic Desktop\"", script, StringComparison.Ordinal);
        Assert.Contains("profile: \"webui-compat/52f9e75\"", script, StringComparison.Ordinal);
        Assert.Contains("sessionCredential:", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Object.defineProperty(globalThis, \"webui\"", script, StringComparison.Ordinal);
    }

    [Test]
    public async Task CapabilityFailuresProduceOnlyStableRedactedDiagnostics()
    {
        var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
        string? invocationCorrelationId = null;
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            DiagnosticSink = diagnostics.Enqueue,
        });
        await using var surface = await host.CreateSurfaceAsync();
        using var registration = surface.RegisterCapability(
            "sample.failure",
            (invocation, _) =>
            {
                invocationCorrelationId = invocation.CorrelationId;
                throw new InvalidOperationException("secret-stack");
            });
        using var client = new HttpClient();
        var bridge = await client.GetStringAsync(new Uri(surface.Url, "webui.js"));
        var token = ExtractUnsigned(bridge, "const TOKEN = ");
        var credential = ExtractQuoted(bridge, "const SESSION_CREDENTIAL = \"");
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", $"{surface.Url.Scheme}://{surface.Url.Authority}");
        socket.Options.AddSubProtocol($"runic-desktop.{credential}");
        await socket.ConnectAsync(ToWebSocketUrl(surface.Url), timeout.Token);

        await SendPacketAsync(socket, CreatePacket(token, 0, 0xF5, [0]), timeout.Token);
        _ = await ReceivePacketAsync(socket, timeout.Token);
        await SendPacketAsync(
            socket,
            CreatePacket(token, 1, 0xF9, [.. "sample.failure\0\0"u8]),
            timeout.Token);
        _ = await ReceivePacketAsync(socket, timeout.Token);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DesktopErrorCategory.OperationFailed, diagnostic.Category);
        Assert.Equal("capability-failed", diagnostic.Code);
        Assert.Equal("The presentation capability failed.", diagnostic.Message);
        Assert.Equal(invocationCorrelationId, diagnostic.CorrelationId);
        Assert.Matches("^[a-f0-9]{32}$", diagnostic.CorrelationId);
        Assert.DoesNotContain("secret-stack", diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Test]
    public async Task ACallToAReleasedCapabilityIsLateAndAnUnadmittedOneNamesTheCapability()
    {
        var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
        var invocations = 0;
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { DiagnosticSink = diagnostics.Enqueue });
        await using var surface = await host.CreateSurfaceAsync();
        PresentationCapabilityHandler handler = (_, _) =>
        {
            Interlocked.Increment(ref invocations);
            return ValueTask.FromResult(PresentationResult.FromString("served"));
        };
        var registration = surface.RegisterCapability("page.unmount", handler);
        using var client = new HttpClient();
        var bridge = await client.GetStringAsync(new Uri(surface.Url, "webui.js"));
        var token = ExtractUnsigned(bridge, "const TOKEN = ");
        var credential = ExtractQuoted(bridge, "const SESSION_CREDENTIAL = \"");
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
        using var socket = await ConnectAuthenticatedAsync(surface.Url, credential, token, timeout.Token);
        ushort id = 0;
        async Task<string> CallAsync(string capability)
        {
            var call = ++id;
            await SendPacketAsync(socket, CreatePacket(token, call, 0xF9, [.. Encoding.UTF8.GetBytes($"{capability}\0\0")]), timeout.Token);
            // Binding announcements can arrive before the reply.
            while (true)
            {
                var packet = await ReceivePacketAsync(socket, timeout.Token);
                if (packet[7] == 0xF9 && BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(5, 2)) == call)
                    return Encoding.UTF8.GetString(packet[PacketHeaderSize..]).TrimEnd('\0');
            }
        }

        Assert.Equal("served", await CallAsync("page.unmount"));
        Assert.Empty(diagnostics);

        // The owner released the capability while the page's call was in flight: late, so no diagnostic and no handler.
        // The reply is the released envelope CS-WebUI sends for an unbound route (Views protocol 3,
        // formerly specs/application/fixtures/released/unbound-route.json).
        const string released = "{\"ok\":false,\"state\":null,\"error\":{\"kind\":\"released\",\"message\":\"This View was released.\"}}";
        registration.Dispose();
        Assert.Equal(released, await CallAsync("page.unmount"));
        Assert.Equal(1, Volatile.Read(ref invocations));
        Assert.Empty(diagnostics);

        // A new registration of the name is served again, and releasing it once more stays benign.
        var replacement = surface.RegisterCapability("page.unmount", handler);
        Assert.Equal("served", await CallAsync("page.unmount"));
        replacement.Dispose();
        Assert.Equal(released, await CallAsync("page.unmount"));
        Assert.Empty(diagnostics);

        // A name that was never admitted is still an error with an empty reply, and names the capability; the page's
        // text is bounded and printable.
        Assert.Equal("", await CallAsync("page.never"));
        var unadmitted = Assert.Single(diagnostics);
        Assert.Equal(DesktopErrorCategory.CapabilityDenied, unadmitted.Category);
        Assert.Equal("capability-not-admitted", unadmitted.Code);
        Assert.Equal(DesktopDiagnosticSeverity.Error, unadmitted.Severity);
        Assert.Equal("The presentation capability 'page.never' is not admitted for this session.", unadmitted.Message);
        _ = await CallAsync("odd\u0007name" + new string('x', 100));
        Assert.Equal(2, diagnostics.Count);
        var bounded = diagnostics.Last().Message;
        Assert.Contains("'odd?namexxx", bounded, StringComparison.Ordinal);
        Assert.Contains("...'", bounded, StringComparison.Ordinal);
        Assert.True(bounded.Length < 140);
    }

    [Test]
    public async Task PresentationSessionIdentityIsUniquePerAuthenticatedConnection()
    {
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            Security = new DesktopSecurityPolicy { ClientAdmission = DesktopClientAdmission.Multiple },
        });
        await using var surface = await host.CreateSurfaceAsync();
        var captured = new ConcurrentQueue<PresentationInvocation>();
        var bothInvocations = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = surface.RegisterCapability("identity", (invocation, _) =>
        {
            captured.Enqueue(invocation);
            if (captured.Count == 2) bothInvocations.TrySetResult();
            return ValueTask.FromResult(PresentationResult.None);
        });
        using var client = new HttpClient();
        var bootstrap = await client.GetStringAsync(new Uri(surface.Url, "webui.js"));
        var token = ExtractUnsigned(bootstrap, "const TOKEN = ");
        var credential = ExtractQuoted(bootstrap, "const SESSION_CREDENTIAL = \"");
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
        using var first = await ConnectAuthenticatedAsync(surface.Url, credential, token, timeout.Token);
        using var second = await ConnectAuthenticatedAsync(surface.Url, credential, token, timeout.Token);

        await SendPacketAsync(first, CreatePacket(token, 1, 0xF9, [.. "identity\0\0"u8]), timeout.Token);
        _ = await ReceivePacketAsync(first, timeout.Token);
        await SendPacketAsync(second, CreatePacket(token, 1, 0xF9, [.. "identity\0\0"u8]), timeout.Token);
        _ = await ReceivePacketAsync(second, timeout.Token);
        await bothInvocations.Task.WaitAsync(timeout.Token);

        var identities = captured.ToArray();
        Assert.Equal(2, identities.Length);
        Assert.All(identities, invocation => Assert.Equal(invocation.SessionId, invocation.Session.Id));
        Assert.NotEqual(identities[0].Session.Id, identities[1].Session.Id);
    }

    [Test]
    public async Task HostSharesListenerAndCreatesARequestScopePerRequest()
    {
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            ConfigureServices = services => services.AddScoped<RequestMarker>(),
        });
        await using var first = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Path = "first",
            Content = new DesktopContent.Handler(static (request, _) => ValueTask.FromResult<ContentResponse?>(
                ContentResponse.Text($"first:{request.Services.GetRequiredService<RequestMarker>().Id}"))),
        });
        await using var second = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Path = "second",
            Content = new DesktopContent.Handler(static (request, _) => ValueTask.FromResult<ContentResponse?>(
                ContentResponse.Text($"second:{request.Services.GetRequiredService<RequestMarker>().Id}"))),
        });
        await using var isolated = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            UseIsolatedListener = true,
            Content = new DesktopContent.Html("isolated"),
        });

        Assert.Equal(first.Url.Port, second.Url.Port);
        Assert.NotEqual(first.Url.Port, isolated.Url.Port);
        using var client = new HttpClient();
        var firstRequest = await client.GetStringAsync(first.Url);
        var secondRequest = await client.GetStringAsync(first.Url);
        Assert.StartsWith("first:", firstRequest, StringComparison.Ordinal);
        Assert.NotEqual(firstRequest, secondRequest);
        Assert.StartsWith("second:", await client.GetStringAsync(second.Url), StringComparison.Ordinal);

        var firstUrl = first.Url;
        await first.CloseAsync();
        using var closedResponse = await client.GetAsync(firstUrl);
        Assert.Equal(HttpStatusCode.NotFound, closedResponse.StatusCode);
        Assert.StartsWith("second:", await client.GetStringAsync(second.Url), StringComparison.Ordinal);
    }

    [Test]
    public async Task ContentRequestExposesAnImmutableCaseInsensitiveHeaderSnapshot()
    {
        IReadOnlyDictionary<string, string>? observed = null;
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Handler((request, _) =>
            {
                observed = request.Headers;
                return ValueTask.FromResult<ContentResponse?>(ContentResponse.Text("ok"));
            }),
        });
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("If-None-Match", "\"asset-v1\"");

        _ = await client.GetStringAsync(surface.Url);

        Assert.NotNull(observed);
        Assert.Equal("\"asset-v1\"", observed["if-none-match"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)observed).Add("x", "y"));
    }

    [Test]
    public async Task OpeningAndClosingWindowPreservesSurfaceContentAndLifetime()
    {
        var factory = new RecordingWindowHostFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            WaitForConnection = false,
        });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Html("original"),
        });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
        });
        using var client = new HttpClient();

        Assert.Equal(surface.Url, factory.Host?.Url);
        Assert.Equal("original", await client.GetStringAsync(surface.Url));
        await window.CloseAsync();
        Assert.Equal("original", await client.GetStringAsync(surface.Url));
    }

    [Test]
    public async Task EmbeddedPresentationReceivesExplicitPermissionPolicy()
    {
        var factory = new RecordingWindowHostFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            WaitForConnection = false,
        });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            AllowedPermissions = DesktopPermissionGrant.MediaCapture,
        });

        Assert.Equal(DesktopPermissionGrant.MediaCapture, factory.Host?.Options?.AllowedPermissions);
        Assert.False(window.IsFallback);
        Assert.Equal(BrowserKind.Embedded, window.Browser);
        Assert.Equal(BrowserKind.Embedded, window.RequestedBrowser);
    }

    [Test]
    public async Task EmbeddedPresentationDeniesSensitivePermissionsByDefault()
    {
        var factory = new RecordingWindowHostFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            WaitForConnection = false,
        });
        await using var surface = await host.CreateSurfaceAsync();
        await using var _ = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
        });

        Assert.Equal(DesktopPermissionGrant.None, factory.Host?.Options?.AllowedPermissions);
    }

    [Test]
    public async Task UnavailablePresentationFailsEarlyWithCorrelatedRemediation()
    {
        var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new UnavailableWindowHostFactory(),
            DiagnosticSink = diagnostics.Enqueue,
            WaitForConnection = false,
        });
        await using var surface = await host.CreateSurfaceAsync();

        var error = await Assert.ThrowsAsync<DesktopException>(() =>
            surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded }).AsTask());

        Assert.Equal("custom-window-host-unavailable", error.Code);
        Assert.NotEmpty(error.CorrelationId);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(error.CorrelationId, diagnostic.CorrelationId);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Remediation));
    }

    [Test]
    public async Task PreflightReportsTheRequestedPolicyAndConcreteCapabilityLimit()
    {
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new UnavailableWindowHostFactory(),
        });

        var requestedOnly = host.GetPresentationPreflight(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
        });
        var explicitFallback = host.GetPresentationPreflight(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
        });

        Assert.Equal(BrowserKind.Embedded, requestedOnly.RequestedBrowser);
        Assert.Equal(DesktopPresentationPolicy.RequestedOnly, requestedOnly.PresentationPolicy);
        Assert.False(requestedOnly.IsAvailable);
        Assert.Equal(DesktopWindowCapabilities.None, requestedOnly.Preferred.Capabilities);
        Assert.Equal("custom-window-host-unavailable", requestedOnly.Diagnostics[0].Code);
        Assert.Equal(DesktopPresentationPolicy.EmbeddedThenBrowser, explicitFallback.PresentationPolicy);
        Assert.NotNull(explicitFallback.Fallback);
        Assert.Equal(
            explicitFallback.Fallback!.IsAvailable && !explicitFallback.Preferred.IsAvailable,
            explicitFallback.WouldFallBack);
    }

    [Test]
    public async Task PreflightRejectsUnknownPresentationPolicies()
    {
        await using var host = await DesktopHost.StartAsync();

        Assert.Throws<ArgumentOutOfRangeException>(() => host.GetPresentationPreflight(new DesktopWindowOptions
        {
            PresentationPolicy = (DesktopPresentationPolicy)42,
        }));
    }

    [Test]
    public async Task ExplicitEmbeddedThenBrowserFallbackIsObservable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Directory.CreateTempSubdirectory("runic-desktop-fallback-");
        try
        {
            var executable = Path.Combine(folder.FullName, OperatingSystem.IsMacOS()
                ? "Google Chrome.app/Contents/MacOS/Google Chrome"
                : "google-chrome");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                BrowserFolder = folder.FullName,
                WindowHostFactory = new UnavailableWindowHostFactory(),
                DiagnosticSink = diagnostics.Enqueue,
                WaitForConnection = false,
            });
            await using var surface = await host.CreateSurfaceAsync();
            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Chrome,
                PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
            });

            Assert.True(window.IsFallback);
            Assert.Equal(BrowserKind.Chrome, window.RequestedBrowser);
            Assert.Equal(BrowserKind.Chrome, window.Browser);
            Assert.Contains(diagnostics, static item => item.Code == "embedded-presentation-fallback");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Test]
    public async Task FallbacksAreTriedInOrder()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Directory.CreateTempSubdirectory("runic-desktop-fallbacks-");
        try
        {
            var executable = Path.Combine(folder.FullName, OperatingSystem.IsMacOS()
                ? "Google Chrome.app/Contents/MacOS/Google Chrome"
                : "google-chrome");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                BrowserFolder = folder.FullName,
                WindowHostFactory = new UnavailableWindowHostFactory(),
                DiagnosticSink = diagnostics.Enqueue,
                WaitForConnection = false,
            });
            await using var surface = await host.CreateSurfaceAsync();
            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded,
                // Safari is detection only, so it fails on every platform before Chrome is tried.
                Fallbacks = [BrowserKind.Safari, BrowserKind.Chrome],
            });

            Assert.True(window.IsFallback);
            Assert.Equal(BrowserKind.Embedded, window.RequestedBrowser);
            Assert.Equal(BrowserKind.Chrome, window.Browser);
            Assert.Contains(diagnostics, static item => item.Code == "embedded-presentation-fallback");
            Assert.Contains(diagnostics, static item => item.Code == "presentation-fallback" &&
                item.Message.Contains("Safari", StringComparison.Ordinal));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Test]
    public async Task ExhaustedFallbacksReportEveryAttempt()
    {
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new UnavailableWindowHostFactory(),
            WaitForConnection = false,
        });
        await using var surface = await host.CreateSurfaceAsync();

        var error = await Assert.ThrowsAsync<DesktopPresentationException>(async () =>
            await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Fallbacks = [BrowserKind.Safari, BrowserKind.Opera],
            }));

        Assert.Equal(
            [BrowserKind.Embedded, BrowserKind.Safari, BrowserKind.Opera],
            error.Attempts.Select(static attempt => attempt.Browser).ToArray());
        Assert.Equal(
            ["custom-window-host-unavailable", "browser-unsupported", "browser-unsupported"],
            error.Attempts.Select(static attempt => attempt.Failure.Code).ToArray());
        // The last attempt's failure classifies the whole failure.
        Assert.Equal("browser-unsupported", error.Code);
        Assert.Same(error.Attempts[^1].Failure, error.InnerException);
        Assert.Equal(error.Attempts[^1].Failure.CorrelationId, error.CorrelationId);
        Assert.Contains("Embedded: custom-window-host-unavailable", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task PreflightListsEveryFallback()
    {
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new UnavailableWindowHostFactory(),
        });

        var preflight = host.GetPresentationPreflight(new DesktopWindowOptions
        {
            Fallbacks = [BrowserKind.Safari, BrowserKind.Opera],
        });

        Assert.Equal(
            [BrowserKind.Safari, BrowserKind.Opera],
            preflight.Fallbacks.Select(static fallback => fallback.Browser).ToArray());
        Assert.Same(preflight.Fallbacks[0], preflight.Fallback);
        Assert.False(preflight.IsAvailable);
        Assert.False(preflight.WouldFallBack);
        Assert.Equal("custom-window-host-unavailable", preflight.Diagnostics[0].Code);
        Assert.Contains(preflight.Diagnostics, static diagnostic => diagnostic.Code == "browser-unsupported");

        // EmbeddedThenBrowser declares its one browser as the only fallback.
        var policy = host.GetPresentationPreflight(new DesktopWindowOptions
        {
            Browser = BrowserKind.Safari,
            PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
        });
        Assert.Equal(BrowserKind.Embedded, policy.Preferred.Browser);
        Assert.Equal(BrowserKind.Safari, Assert.Single(policy.Fallbacks).Browser);

        // A non-empty list replaces it.
        var replaced = host.GetPresentationPreflight(new DesktopWindowOptions
        {
            PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
            Fallbacks = [BrowserKind.Opera],
        });
        Assert.Equal(BrowserKind.Opera, Assert.Single(replaced.Fallbacks).Browser);
    }

    [Test]
    public async Task FallbacksRejectAmbiguousChains()
    {
        await using var host = await DesktopHost.StartAsync();

        Assert.Throws<ArgumentException>(() => host.GetPresentationPreflight(new DesktopWindowOptions
        {
            Fallbacks = [BrowserKind.Embedded],
        }));
        Assert.Throws<ArgumentException>(() => host.GetPresentationPreflight(new DesktopWindowOptions
        {
            Fallbacks = [BrowserKind.Chrome, BrowserKind.Chrome],
        }));
        Assert.Throws<ArgumentException>(() => host.GetPresentationPreflight(new DesktopWindowOptions
        {
            Browser = BrowserKind.Chrome,
            PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
            Fallbacks = [BrowserKind.Firefox],
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => host.GetPresentationPreflight(new DesktopWindowOptions
        {
            Fallbacks = [(BrowserKind)42],
        }));
        var invalid = host.Validate(new DesktopWindowOptions { Fallbacks = [BrowserKind.Embedded] });
        Assert.Contains(invalid.Errors, static diagnostic => diagnostic.Code == "window-option-invalid");
    }

    [Test]
    public async Task EmbeddedConnectionTimeoutIsReportedInsteadOfFallingBack()
    {
        // On the test's clock, isolated from requests outside the test (TestClients).
        using var isolation = TestClients.Isolate();
        using var bound = TestTimeouts.Cancellation(TimeSpan.FromSeconds(30));
        var time = new ManualTime();
        var factory = new RecordingWindowHostFactory();
        var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            DiagnosticSink = diagnostics.Enqueue,
            ConnectionTimeout = TimeSpan.FromMilliseconds(200),
            TimeProvider = time,
        });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Html("never connects"),
        });

        var opening = surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
        }).AsTask();
        await time.WaitForTimerAsync(TimeSpan.FromMilliseconds(200), bound.Token);
        time.Advance(TimeSpan.FromMilliseconds(200));
        var exception = await Assert.ThrowsAsync<DesktopException>(() => opening.WaitAsync(bound.Token));

        Assert.Equal(DesktopErrorCategory.TimedOut, exception.Category);
        Assert.Equal("presentation-connection-timeout", exception.Code);
        var timeout = Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Contains("no request reached the server", timeout.Message);
        Assert.Contains(diagnostics, static item => item.Code == "presentation-connection-timeout");
        Assert.DoesNotContain(diagnostics, static item => item.Code == "embedded-presentation-fallback");
        Assert.False(factory.Host?.IsOpen);
        // Only the presentation closed; the surface keeps serving for its owner.
        using var client = TestClients.Http();
        Assert.Equal("never connects", await client.GetStringAsync(surface.Url));
    }

    [Test]
    public async Task ConnectionTimeoutReportsHowFarThePageGot()
    {
        using var isolation = TestClients.Isolate();
        using var bound = TestTimeouts.Cancellation(TimeSpan.FromSeconds(30));
        var time = new ManualTime();
        var factory = new RecordingWindowHostFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            ConnectionTimeout = TimeSpan.FromSeconds(2),
            TimeProvider = time,
        });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Html("no bridge script"),
        });
        var opening = surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
        }).AsTask();
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(2), bound.Token);
        using var client = TestClients.Http();
        Assert.Equal("no bridge script", await client.GetStringAsync(surface.Url));

        time.Advance(TimeSpan.FromSeconds(2));
        var exception = await Assert.ThrowsAsync<DesktopException>(() => opening.WaitAsync(bound.Token));

        var timeout = Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Contains("the page was requested, but not /webui.js", timeout.Message);
    }

    [Test]
    public void AvailabilityReportsConcretePresentationsAndActionableFailures()
    {
        var availability = DesktopPlatform.GetAvailability(new() { BrowserFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) });

        Assert.False(string.IsNullOrWhiteSpace(availability.Platform));
        Assert.Contains(availability.Presentations, static item => item.Browser == BrowserKind.Embedded);
        Assert.DoesNotContain(availability.Presentations, static item => item.Browser == BrowserKind.Any);
        Assert.All(
            availability.Diagnostics,
            static diagnostic => Assert.False(string.IsNullOrWhiteSpace(diagnostic.Remediation)));
    }

    [Test]
    public async Task PublicStreamingResponseDoesNotOpenForHead()
    {
        var factoryCalls = 0;
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Handler((request, _) => ValueTask.FromResult<ContentResponse?>(
                request.Path == "/stream"
                    ? ContentResponse.Stream(
                        _ =>
                        {
                            Interlocked.Increment(ref factoryCalls);
                            return ValueTask.FromResult<Stream>(new MemoryStream("stream"u8.ToArray()));
                        },
                        contentLength: 6)
                    : null)),
        });
        using var client = new HttpClient();

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, new Uri(surface.Url, "stream"));
        using var headResponse = await client.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
        Assert.Equal(6, headResponse.Content.Headers.ContentLength);
        Assert.Equal(0, Volatile.Read(ref factoryCalls));

        Assert.Equal("stream", await client.GetStringAsync(new Uri(surface.Url, "stream")));
        Assert.Equal(1, Volatile.Read(ref factoryCalls));
    }

    [Test]
    [DisplayName("MediaCaptureGrantIsBoundToThePresentedOrigin(requester: $requester, expected: $expected)")]
    [Arguments("http://127.0.0.1:4100/entry/index.html", true)]
    [Arguments("HTTP://127.0.0.1:4100/", true)]
    [Arguments("http://127.0.0.1:4101/", false)]
    [Arguments("http://localhost:4100/", false)]
    [Arguments("https://127.0.0.1:4100/", false)]
    [Arguments("about:blank", false)]
    [Arguments(null, false)]
    public void MediaCaptureGrantIsBoundToThePresentedOrigin(string? requester, bool expected) =>
        Assert.Equal(expected, Internal.PresentationSecurityPolicy.IsSameOrigin(new Uri("http://127.0.0.1:4100/surface/"), requester));

    // SEC-37 on macOS: WKUIDelegate's media capture decision.
    [Test]
    [DisplayName("MacOsMediaCaptureFollowsTheGrantAndThePresentedOrigin(granted: $granted, origin: $origin, frame: $frame, top: $top, type: $type, expected: $expected)")]
    [Arguments(true, "http://127.0.0.1:4100", "http://127.0.0.1:4100", "http://127.0.0.1:4100/surface/index.html", 0, true)]
    [Arguments(true, "http://127.0.0.1:4100", "http://127.0.0.1:4100", "http://127.0.0.1:4100/surface/", 1, true)]
    [Arguments(true, "http://127.0.0.1:4100", "http://127.0.0.1:4100", "http://127.0.0.1:4100/surface/", 2, true)]
    [Arguments(false, "http://127.0.0.1:4100", "http://127.0.0.1:4100", "http://127.0.0.1:4100/surface/", 0, false)]
    [Arguments(true, "http://127.0.0.1:4100", "http://127.0.0.1:4100", "http://127.0.0.1:4100/surface/", 3, false)]
    [Arguments(true, "https://camera.example", "https://camera.example", "http://127.0.0.1:4100/surface/", 0, false)]
    [Arguments(true, "http://127.0.0.1:4100", "https://camera.example", "http://127.0.0.1:4100/surface/", 0, false)]
    [Arguments(true, "http://127.0.0.1:4100", "http://127.0.0.1:4100", "https://camera.example/", 0, false)]
    [Arguments(true, null, "http://127.0.0.1:4100", "http://127.0.0.1:4100/surface/", 0, false)]
    [Arguments(true, "http://127.0.0.1:4100", null, "http://127.0.0.1:4100/surface/", 0, false)]
    public void MacOsMediaCaptureFollowsTheGrantAndThePresentedOrigin(
        bool granted, string? origin, string? frame, string? top, int type, bool expected) =>
        Assert.Equal(
            expected ? Internal.MacOsWebContent.PermissionGrant : Internal.MacOsWebContent.PermissionDeny,
            Internal.MacOsWebContent.ForMediaCapture(granted, new Uri("http://127.0.0.1:4100/surface/"), origin, frame, top, type));

    [Test]
    public void MacOsSecurityOriginsBecomeOriginUrls()
    {
        Assert.Equal("http://127.0.0.1:4100", Internal.MacOsWebContent.OriginUrl("http", "127.0.0.1", 4100));
        Assert.Equal("https://example.com", Internal.MacOsWebContent.OriginUrl("https", "example.com", 0));
        Assert.Equal("http://[::1]:4100", Internal.MacOsWebContent.OriginUrl("http", "::1", 4100));
        Assert.Null(Internal.MacOsWebContent.OriginUrl("", "", 0));
        Assert.Null(Internal.MacOsWebContent.OriginUrl("file", null, 0));
    }

    // SEC-37 on WebKitGTK: the subframes the isolated-world script reports decide whether the page may use the grant.
    [Test]
    public void WebKitGtkMediaGrantRequiresSubframesOfThePresentedOrigin()
    {
        var frames = new Internal.WebKitGtkMediaFrames(new Uri("http://127.0.0.1:4100/surface/"));
        Assert.False(frames.HasForeignFrames);

        frames.Receive("+a http://127.0.0.1:4100");
        Assert.False(frames.HasForeignFrames);
        frames.Receive("+b https://camera.example");
        Assert.True(frames.HasForeignFrames);
        frames.Receive("+c null");
        frames.Receive("-b");
        Assert.True(frames.HasForeignFrames);
        frames.Receive("-c");
        Assert.False(frames.HasForeignFrames);

        frames.Receive("+d http://127.0.0.1:4101");
        frames.Receive("+e http://localhost:4100");
        Assert.True(frames.HasForeignFrames);
        frames.Receive("0");
        Assert.False(frames.HasForeignFrames);

        frames.Receive("+f about:blank");
        Assert.True(frames.HasForeignFrames);
        frames.Reset();
        Assert.False(frames.HasForeignFrames);

        foreach (var malformed in new[] { null, "", "+", "+ http://evil.example", "+g", "-", "00", "x" })
        {
            frames.Receive(malformed);
        }
        Assert.False(frames.HasForeignFrames);
    }

    // SEC-05: surface pages may be framed only by the surface's own origin and its additional origins.
    [Test]
    public async Task SurfaceResponsesRefuseFramingByOtherOrigins()
    {
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("safe") });
        await using var additional = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Html("safe"),
            Security = DesktopSecurityPolicy.Default with
            {
                AdditionalOrigins = [new Uri("https://Dev.Example"), new Uri("http://localhost:5173")],
            },
        });
        using var client = new HttpClient();

        using var response = await client.GetAsync(surface.Url);
        Assert.Equal("frame-ancestors 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("SAMEORIGIN", response.Headers.GetValues("X-Frame-Options").Single());
        using var missing = await client.GetAsync(new Uri(surface.Url, "missing.html"));
        Assert.Equal("frame-ancestors 'self'", missing.Headers.GetValues("Content-Security-Policy").Single());

        using var withOrigins = await client.GetAsync(additional.Url);
        Assert.Equal(
            "frame-ancestors 'self' http://localhost:5173 https://dev.example:443",
            withOrigins.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("SAMEORIGIN", withOrigins.Headers.GetValues("X-Frame-Options").Single());
    }

    // SEC-06: DesktopSurfaceOptions.ContentSecurityPolicy is a second header on HTML, beside the framing policy, and a
    // handler's own policy no longer replaces the framing policy.
    [Test]
    public async Task SurfacePolicyIsASecondHeaderOnHtml()
    {
        const string Policy = "default-src 'self'; object-src 'none'";
        const string HandlerPolicy = "script-src 'none'";
        var root = Directory.CreateTempSubdirectory("runic-csp-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "index.html"), "<p>file</p>");
            File.WriteAllText(Path.Combine(root, "app.js"), "1");
            await using var host = await DesktopHost.StartAsync();
            await using var html = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Html("<p>safe</p>"),
                ContentSecurityPolicy = Policy,
            });
            await using var directory = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Directory(root, "index.html"),
                ContentSecurityPolicy = Policy,
            });
            await using var handler = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Handler((request, _) => ValueTask.FromResult<ContentResponse?>(
                    request.Path.EndsWith(".js", StringComparison.Ordinal)
                        ? new ContentResponse("1"u8.ToArray(), "text/javascript", 200, new Dictionary<string, string> { ["Content-Security-Policy"] = HandlerPolicy })
                        : new ContentResponse("<p>handled</p>"u8.ToArray(), "text/html; charset=utf-8", 200, new Dictionary<string, string> { ["Content-Security-Policy"] = HandlerPolicy }))),
                ContentSecurityPolicy = Policy,
            });
            await using var handlerOnly = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Handler((_, _) => ValueTask.FromResult<ContentResponse?>(
                    new ContentResponse("<p>handled</p>"u8.ToArray(), "text/html", 200, new Dictionary<string, string> { ["Content-Security-Policy"] = Policy }))),
                ContentSecurityPolicy = Policy,
            });
            using var client = new HttpClient();
            async Task<string[]> PoliciesAsync(Uri url)
            {
                using var response = await client.GetAsync(url);
                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
                return [.. response.Headers.GetValues("Content-Security-Policy")];
            }

            Assert.Equal(["frame-ancestors 'self'", Policy], await PoliciesAsync(html.Url));
            Assert.Equal(["frame-ancestors 'self'", Policy], await PoliciesAsync(new Uri(directory.Url, "index.html")));
            Assert.Equal(["frame-ancestors 'self'"], await PoliciesAsync(new Uri(directory.Url, "app.js")));
            Assert.Equal(["frame-ancestors 'self'", HandlerPolicy, Policy], await PoliciesAsync(handler.Url));
            Assert.Equal(["frame-ancestors 'self'", HandlerPolicy], await PoliciesAsync(new Uri(handler.Url, "app.js")));
            // The same policy from the handler and the surface is sent once.
            Assert.Equal(["frame-ancestors 'self'", Policy], await PoliciesAsync(handlerOnly.Url));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        foreach (var invalid in new[] { "", " ", "default-src 'self'\r\nX-Injected: 1", "a\0b", "caf\u00e9" })
        {
            Assert.Throws<ArgumentException>(() => new DesktopSurfaceOptions { ContentSecurityPolicy = invalid });
        }
    }

    // SEC-24: the WebSocket subprotocol carrying the credential is compared in constant time, and only whole.
    [Test]
    public void SessionCredentialMatchesOnlyAWholeOfferedProtocol()
    {
        const string expected = "runic-desktop.0123456789abcdef";
        Assert.True(WebUiWindow.OffersCredential(["other", expected], expected));
        Assert.False(WebUiWindow.OffersCredential([], expected));
        Assert.False(WebUiWindow.OffersCredential(["runic-desktop.0123456789abcde"], expected));
        Assert.False(WebUiWindow.OffersCredential([expected + "0"], expected));
        Assert.False(WebUiWindow.OffersCredential(["RUNIC-DESKTOP.0123456789ABCDEF"], expected));
    }

    [Test]
    public async Task LocalContentDeniesMediaCaptureByDefault()
    {
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("safe") });
        using var client = new HttpClient();

        using var response = await client.GetAsync(surface.Url);

        Assert.Equal("camera=(), microphone=()", response.Headers.GetValues("Permissions-Policy").Single());
    }

    // #313: an embedded window reports its own events, such as a crashed WebKitGTK web process, through
    // DesktopWindowHostOptions.DiagnosticSink to the sink and the Desktop logger (event 3010), and a failing sink
    // does not reach the window.
    [Test]
    public async Task EmbeddedWindowDiagnosticsReachTheSinkAndTheLog()
    {
        var factory = new RecordingWindowHostFactory();
        var loggers = new ConfigurationValidationTests.RecordingLoggerFactory();
        var reported = new ConcurrentQueue<DesktopDiagnostic>();
        var failSink = false;
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            WaitForConnection = false,
            LoggerFactory = loggers,
            DiagnosticSink = diagnostic =>
            {
                reported.Enqueue(diagnostic);
                if (failSink) throw new InvalidOperationException("sink");
            },
        });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<p>page</p>") });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
        var report = factory.Host?.Options?.DiagnosticSink;
        Assert.NotNull(report);

        var crashed = new DesktopDiagnostic(DesktopErrorCategory.OperationFailed, "web-process-terminated",
            "The window's WebKitGTK web process crashed; the page was reloaded.", Retryable: true, Remediation: "Look.")
        {
            Severity = DesktopDiagnosticSeverity.Warning,
        };
        report(crashed);
        Assert.Same(crashed, Assert.Single(reported));
        var logged = Assert.Single(loggers.Entries, entry => entry.EventId.Id == 3010);
        Assert.Equal("EmbeddedWindowDiagnostic", logged.EventId.Name);
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Equal("Runic.Desktop", logged.Category);
        Assert.Contains("web-process-terminated", logged.Message, StringComparison.Ordinal);

        failSink = true;
        report(crashed with { Severity = DesktopDiagnosticSeverity.Error });
        Assert.Contains(loggers.Entries, entry => entry.EventId.Id == 3009);
        Assert.Contains(loggers.Entries, entry => entry.EventId.Id == 3010 && entry.Level == LogLevel.Error);

        // The Linux global menu's diagnostics reach the sink and are logged as its own events (3016, 3015), not 3010.
        failSink = false;
        var before = loggers.Entries.Count(static entry => entry.EventId.Id == 3010);
        report(new DesktopDiagnostic(DesktopErrorCategory.Unavailable, "global-menu-failed", "The registrar refused the window.", Retryable: true)
        {
            Severity = DesktopDiagnosticSeverity.Warning,
        });
        report(new DesktopDiagnostic(DesktopErrorCategory.Unavailable, "global-menu-permission", "No registrar in the sandbox.", Retryable: true,
            Remediation: "Add --talk-name=com.canonical.AppMenu.Registrar.") { Severity = DesktopDiagnosticSeverity.Warning });
        Assert.Contains(reported, static diagnostic => diagnostic.Code == "global-menu-failed");
        var failed = Assert.Single(loggers.Entries, static entry => entry.EventId.Id == 3016);
        Assert.Equal(("GlobalMenuFailed", LogLevel.Warning), (failed.EventId.Name, failed.Level));
        Assert.Contains("The registrar refused the window.", failed.Message, StringComparison.Ordinal);
        var permission = Assert.Single(loggers.Entries, static entry => entry.EventId.Id == 3015);
        Assert.Equal(("MenuBarPlacementChanged", LogLevel.Information), (permission.EventId.Name, permission.Level));
        Assert.Contains("--talk-name=com.canonical.AppMenu.Registrar", permission.Message, StringComparison.Ordinal);
        Assert.Equal(before, loggers.Entries.Count(static entry => entry.EventId.Id == 3010));
    }

    [Test]
    public async Task DirectoryContentOpensItsEntryAndServesItsFiles()
    {
        var root = Directory.CreateTempSubdirectory("runic-desktop-typed-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "main.html"), "main");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "index.html"), "index");
            Directory.CreateDirectory(Path.Combine(root.FullName, "nested"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "nested", "app.js"), "script");
            var factory = new RecordingWindowHostFactory();
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                WindowHostFactory = factory,
                WaitForConnection = false,
            });
            using var client = new HttpClient();

            await using (var entry = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Directory(root.FullName, "main.html"),
            }))
            {
                Assert.Equal("main", await client.GetStringAsync(entry.Url));
                Assert.Equal("script", await client.GetStringAsync(new Uri(entry.Url, "nested/app.js")));
                await using var window = await entry.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
                Assert.Equal(new Uri(entry.Url, "main.html"), factory.Host?.Url);
            }

            await using var discovered = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Directory(root.FullName),
            });
            Assert.Equal("index", await client.GetStringAsync(discovered.Url));

            // #313: the redirect to the index keeps the query, such as a route the page reads from location.search,
            // and stays under the surface's path.
            using var noRedirects = new HttpClientHandler { AllowAutoRedirect = false };
            using var manual = new HttpClient(noRedirects);
            using var redirect = await manual.GetAsync(new Uri(discovered.Url, "?view=changes&q=a%20b"));
            Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
            Assert.Equal(new Uri(discovered.Url, "index.html?view=changes&q=a%20b"),
                new Uri(discovered.Url, redirect.Headers.Location!));
            Assert.Equal("index", await client.GetStringAsync(new Uri(discovered.Url, "?view=changes")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // #313: the resources serve what the content does not, before 404, for every kind of content.
    [Test]
    public async Task ResourcesServeWhatTheContentDoesNot()
    {
        var root = Directory.CreateTempSubdirectory("runic-desktop-resources-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "index.html"), "index");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "shared.txt"), "content");
            var asked = new ConcurrentQueue<string>();
            ContentHandler resources = (request, _) =>
            {
                asked.Enqueue($"{request.Method} {request.Path}");
                return ValueTask.FromResult<ContentResponse?>(request.Path switch
                {
                    "/icons/app.svg" => ContentResponse.Text("<svg/>", "image/svg+xml"),
                    "/shared.txt" => ContentResponse.Text("resource", "text/plain"),
                    "/favicon.svg" => ContentResponse.Text("<svg>icon</svg>", "image/svg+xml"),
                    "/fails" => throw new InvalidOperationException("The resource failed."),
                    _ => null,
                });
            };
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WaitForConnection = false });
            using var client = new HttpClient();

            await using (var directory = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Directory(root.FullName),
                Resources = resources,
            }))
            {
                Assert.Equal("index", await client.GetStringAsync(directory.Url));
                using var icon = await client.GetAsync(new Uri(directory.Url, "icons/app.svg"));
                Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
                Assert.Equal("image/svg+xml", icon.Content.Headers.ContentType?.MediaType);
                Assert.Equal("<svg/>", await icon.Content.ReadAsStringAsync());
                using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, new Uri(directory.Url, "icons/app.svg")));
                Assert.Equal(HttpStatusCode.OK, head.StatusCode);
                // The content's file wins over a resource of the same path, and the favicon the content lacks is the
                // resource's rather than the default one.
                Assert.Equal("content", await client.GetStringAsync(new Uri(directory.Url, "shared.txt")));
                Assert.Equal("<svg>icon</svg>", await client.GetStringAsync(new Uri(directory.Url, "favicon.svg")));
                using var missing = await client.GetAsync(new Uri(directory.Url, "icons/other.svg"));
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
                using var failed = await client.GetAsync(new Uri(directory.Url, "fails"));
                Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
                Assert.DoesNotContain("GET /shared.txt", asked);
                Assert.Contains("HEAD /icons/app.svg", asked);
            }

            await using var html = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Html("<!doctype html>"),
                Resources = resources,
            });
            Assert.Equal("<!doctype html>", await client.GetStringAsync(html.Url));
            Assert.Equal("<svg/>", await client.GetStringAsync(new Uri(html.Url, "icons/app.svg")));
            Assert.Equal("resource", await client.GetStringAsync(new Uri(html.Url, "shared.txt")));

            await using var without = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Html("<!doctype html>"),
            });
            using var none = await client.GetAsync(new Uri(without.Url, "icons/app.svg"));
            Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // #318: an embedded window that advertises announcements receives them with their priority; another window, and a
    // browser, report the capability unavailable.
    [Test]
    public async Task AnnouncementsReachAWindowHostThatAdvertisesThem()
    {
        var announcing = new RecordingWindowHostFactory { Announces = true };
        await using (var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = announcing, WaitForConnection = false }))
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") }))
        {
            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
            Assert.True(window.Capabilities.HasFlag(DesktopWindowCapabilities.Announcements));
            await window.AnnounceAsync("Saved");
            await window.AnnounceAsync("The disk is full", DesktopAnnouncementPriority.Assertive);
            Assert.Equal([("Saved", DesktopAnnouncementPriority.Polite), ("The disk is full", DesktopAnnouncementPriority.Assertive)],
                announcing.Host!.Announcements.ToArray());
            await Assert.ThrowsAsync<ArgumentException>(async () => await window.AnnounceAsync(" "));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await window.AnnounceAsync("x", (DesktopAnnouncementPriority)7));
        }

        var silent = new RecordingWindowHostFactory();
        await using (var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = silent, WaitForConnection = false }))
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") }))
        {
            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
            Assert.False(window.Capabilities.HasFlag(DesktopWindowCapabilities.Announcements));
            var error = await Assert.ThrowsAsync<DesktopException>(async () => await window.AnnounceAsync("Saved"));
            Assert.Equal("window-capability-unavailable", error.Code);
            Assert.Empty(silent.Host!.Announcements);
        }
    }

    // I-024: an embedded window hides and shows without closing.
    [Test]
    public async Task EmbeddedWindowsHideAndShowWithoutClosing()
    {
        var factory = new RecordingWindowHostFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });

        Assert.True(window.Capabilities.HasFlag(DesktopWindowCapabilities.Visibility));
        await window.HideAsync();
        Assert.True(window.IsOpen);
        await window.ShowAsync();
        await window.SetVisibleAsync(false);
        await window.SetVisibleAsync(true);
        Assert.Equal([false, true, false, true], factory.Host!.Visibility.ToArray());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await window.SetVisibleAsync(true, cancelled.Token));

        await window.CloseAsync();
        Assert.Equal(DesktopWindowCapabilities.None, window.Capabilities);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await window.HideAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await window.ShowAsync());
    }

    // I-024: Closed is raised once, on the thread pool, whatever closed the window; a throwing handler does not stop
    // the others.
    [Test]
    public async Task ClosedIsRaisedOnceWhenTheUserOrTheApplicationClosesTheWindow()
    {
        var factory = new RecordingWindowHostFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });

        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") }))
        {
            var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
            var raised = 0;
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => throw new InvalidOperationException("A failing handler.");
            window.Closed += (sender, _) =>
            {
                Interlocked.Increment(ref raised);
                closed.TrySetResult(ReferenceEquals(sender, window) && Thread.CurrentThread.IsThreadPoolThread && !window.IsOpen);
            };
            await Task.Delay(50);
            Assert.Equal(0, Volatile.Read(ref raised));

            factory.Host!.CloseByUser();
            Assert.True(await closed.Task.WaitScaledAsync(TimeSpan.FromSeconds(10)));
            await window.WaitForCloseAsync().AsTask().WaitScaledAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(200);
            Assert.Equal(1, Volatile.Read(ref raised));
        }

        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") }))
        {
            var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            await window.CloseAsync();
            await closed.Task.WaitScaledAsync(TimeSpan.FromSeconds(10));

            // Disposing the surface closes its window too.
            var next = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
            var nextClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            next.Closed += (_, _) => nextClosed.TrySetResult();
            await surface.DisposeAsync();
            await nextClosed.Task.WaitScaledAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task OnlyDirectoryContentServesLocalFiles()
    {
        // Before 0.7, every surface also served the current directory.
        var name = $"runic-typed-{Guid.NewGuid():N}.txt";
        var file = Path.Combine(Environment.CurrentDirectory, name);
        try
        {
            await File.WriteAllTextAsync(file, "local file");
            await using var host = await DesktopHost.StartAsync();
            using var client = new HttpClient();
            foreach (DesktopContent content in new DesktopContent[]
            {
                new DesktopContent.Html("<p>inline</p>"),
                new DesktopContent.Handler(static (_, _) => ValueTask.FromResult<ContentResponse?>(null)),
                new DesktopContent.ExternalUrl(new Uri("https://example.test/")),
            })
            {
                await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = content });
                using var response = await client.GetAsync(new Uri(surface.Url, name));
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }

            await using var directory = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Directory(Environment.CurrentDirectory),
            });
            Assert.Equal("local file", await client.GetStringAsync(new Uri(directory.Url, name)));
            await using var html = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Html("<p>inline</p>"),
            });
            Assert.Equal("<p>inline</p>", await client.GetStringAsync(html.Url));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public async Task ExternalUrlContentOpensTheUrl()
    {
        var factory = new RecordingWindowHostFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            WaitForConnection = false,
        });
        var external = new Uri("https://example.test/path?a=1");
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.ExternalUrl(external),
        });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });

        Assert.Equal(external, factory.Host?.Url);
    }

    [Test]
    public async Task TypedContentRejectsInvalidValuesWhenConstructed()
    {
        Assert.Throws<ArgumentException>(() => new DesktopContent.ExternalUrl(new Uri("file:///tmp/index.html")));
        Assert.Throws<ArgumentException>(() => new DesktopContent.ExternalUrl(new Uri("index.html", UriKind.Relative)));
        Assert.Throws<ArgumentException>(() => new DesktopContent.Directory("www", "../index.html"));
        Assert.Throws<ArgumentException>(() => new DesktopContent.Directory("www", Path.GetFullPath("index.html")));
        Assert.Throws<ArgumentException>(() => new DesktopContent.Directory(" "));
        Assert.Throws<ArgumentNullException>(() => new DesktopContent.Html(null!));
        Assert.Throws<ArgumentNullException>(() => new DesktopContent.Handler(null!));

        // Object initializers and `with` expressions run the same checks as the constructors.
        var url = new DesktopContent.ExternalUrl(new Uri("https://example.test/"));
        Assert.Throws<ArgumentException>(() => url with { Url = new Uri("file:///etc/passwd") });
        Assert.Throws<ArgumentException>(() => url with { Url = new Uri("javascript:alert(1)") });
        Assert.Throws<ArgumentNullException>(() => url with { Url = null! });
        Assert.Throws<ArgumentException>(() => new DesktopContent.ExternalUrl(new Uri("https://example.test/")) { Url = new Uri("file:///tmp/a") });
        var directory = new DesktopContent.Directory("www", "index.html");
        Assert.Throws<ArgumentException>(() => directory with { Entry = "../secret.txt" });
        Assert.Throws<ArgumentException>(() => directory with { Entry = Path.GetFullPath("index.html") });
        Assert.Throws<ArgumentException>(() => directory with { Root = "" });
        Assert.Null((directory with { Entry = null }).Entry);
        // An entry is a file, not a URL: a query or fragment fails at once with a coded diagnostic instead of
        // a window that waits for a page that never loads.
        foreach (var entry in new[] { "index.html?mode=edit", "index.html#settings", "settings/#/tab" })
        {
            var constructed = Assert.Throws<DesktopConfigurationException>(() => new DesktopContent.Directory("www", entry));
            var initialized = Assert.Throws<DesktopConfigurationException>(() => directory with { Entry = entry });
            foreach (var error in new[] { constructed, initialized })
            {
                var diagnostic = Assert.Single(error.Diagnostics);
                Assert.Equal("content-entry-invalid", diagnostic.Code);
                Assert.Equal("DesktopContent.Directory.Entry", diagnostic.Option);
                Assert.Contains(entry, error.Message, StringComparison.Ordinal);
                Assert.Contains("content-entry-invalid", error.Message, StringComparison.Ordinal);
            }
        }
        Assert.Throws<ArgumentNullException>(() => new DesktopContent.Html("<p></p>") with { Document = null! });
        Assert.Throws<ArgumentNullException>(() => new DesktopContent.Handler(static (_, _) => ValueTask.FromResult<ContentResponse?>(null)) with { Resolve = null! });
        Assert.Equal(new Uri("http://example.test/next"), (url with { Url = new Uri("http://example.test/next") }).Url);

        await using var host = await DesktopHost.StartAsync();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Directory(Path.Combine(Path.GetTempPath(), $"runic-missing-{Guid.NewGuid():N}")),
        }));

        // An entry the frontend build did not emit fails at once, naming the page and the pages that exist,
        // instead of a window that times out waiting for its Runic client script.
        var root = Directory.CreateTempSubdirectory("runic-entry-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "index.html"), "<p></p>");
            Directory.CreateDirectory(Path.Combine(root, "pages"));
            File.WriteAllText(Path.Combine(root, "pages", "settings.html"), "<p></p>");
            var missing = await Assert.ThrowsAsync<DesktopConfigurationException>(async () => await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Directory(root, "about.html"),
            }));
            var notFound = Assert.Single(missing.Diagnostics);
            Assert.Equal("content-entry-not-found", notFound.Code);
            Assert.Equal(DesktopErrorCategory.NotFound, notFound.Category);
            Assert.Equal("DesktopContent.Directory.Entry", notFound.Option);
            Assert.Contains("\"about.html\"", notFound.Message, StringComparison.Ordinal);
            Assert.Contains("Known pages: index.html, pages/settings.html.", notFound.Message, StringComparison.Ordinal);
            await using var nested = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Directory(root, "pages/settings.html"),
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ClosingSurfaceReportsCauseAndReleasesPublicStream()
    {
        ContentRequest? observedRequest = null;
        var stream = new CancellationProbeStream();
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Handler((request, _) =>
            {
                observedRequest = request;
                return ValueTask.FromResult<ContentResponse?>(
                    request.Path == "/stream"
                        ? ContentResponse.Stream(_ => ValueTask.FromResult<Stream>(stream))
                        : null);
            }),
        });
        using var client = new HttpClient();
        using var response = await client.GetAsync(
            new Uri(surface.Url, "stream"),
            HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[1];
        Assert.Equal(1, await body.ReadAsync(buffer));

        await surface.CloseAsync();

        Assert.Equal(RequestCancellationReason.SurfaceClosing, observedRequest?.CancellationReason);
        await stream.Disposed.WaitScaledAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ExecuteJavaScriptReturnsTheWholeResultAndStopsWithItsToken()
    {
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync();
        using var client = new HttpClient();
        var bridge = await client.GetStringAsync(new Uri(surface.Url, "webui.js"));
        var token = ExtractUnsigned(bridge, "const TOKEN = ");
        var credential = ExtractQuoted(bridge, "const SESSION_CREDENTIAL = \"");
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
        using var socket = await ConnectAuthenticatedAsync(surface.Url, credential, token, timeout.Token);
        async Task<ushort> ReceiveScriptAsync(string script)
        {
            while (true)
            {
                var packet = await ReceivePacketAsync(socket, timeout.Token);
                if (packet[7] != 0xFE) continue;
                Assert.Equal(script, Encoding.UTF8.GetString(packet[PacketHeaderSize..]).TrimEnd('\0'));
                return BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(5, 2));
            }
        }

        // A result larger than the former 4 KiB default response buffer is returned whole.
        var expected = new string('x', 10_000);
        var large = surface.ExecuteJavaScriptAsync("return large;", timeout.Token);
        var id = await ReceiveScriptAsync("return large;");
        await SendPacketAsync(socket, CreatePacket(token, id, 0xFE, [0, .. Encoding.UTF8.GetBytes(expected), 0]), timeout.Token);
        Assert.Equal(expected, await large);

        // The token, not a separate timeout, ends the wait.
        using var stop = new CancellationTokenSource();
        var pending = surface.ExecuteJavaScriptAsync("return never;", stop.Token).AsTask();
        _ = await ReceiveScriptAsync("return never;");
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private sealed class RequestMarker
    {
        internal Guid Id { get; } = Guid.NewGuid();
    }

    private static uint ExtractUnsigned(string script, string prefix)
    {
        var start = script.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var end = script.IndexOf(';', start);
        return uint.Parse(script[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ExtractQuoted(string script, string prefix)
    {
        var start = script.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var end = script.IndexOf('"', start);
        return script[start..end];
    }

    private static Uri ToWebSocketUrl(Uri surfaceUrl) => new UriBuilder(surfaceUrl)
    {
        Scheme = "ws",
        Path = $"{surfaceUrl.AbsolutePath}_webui_ws_connect",
    }.Uri;

    private static async Task<ClientWebSocket> ConnectAuthenticatedAsync(
        Uri surfaceUrl, string credential, uint token, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader("Origin", $"{surfaceUrl.Scheme}://{surfaceUrl.Authority}");
            socket.Options.AddSubProtocol($"runic-desktop.{credential}");
            await socket.ConnectAsync(ToWebSocketUrl(surfaceUrl), cancellationToken);
            await SendPacketAsync(socket, CreatePacket(token, 0, 0xF5, [0]), cancellationToken);
            _ = await ReceivePacketAsync(socket, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static byte[] CreatePacket(uint token, ushort id, byte command, byte[] payload)
    {
        var packet = new byte[PacketHeaderSize + payload.Length];
        packet[0] = 0xDD;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), token);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5, 2), id);
        packet[7] = command;
        payload.CopyTo(packet, PacketHeaderSize);
        return packet;
    }

    private static Task SendPacketAsync(
        ClientWebSocket socket,
        byte[] packet,
        CancellationToken cancellationToken) =>
        socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);

    private static async Task<byte[]> ReceivePacketAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var packet = new ArrayBufferWriter<byte>();
        WebSocketReceiveResult response;
        do
        {
            response = await socket.ReceiveAsync(buffer, cancellationToken);
            packet.Write(buffer.AsSpan(0, response.Count));
        }
        while (!response.EndOfMessage);
        return packet.WrittenSpan.ToArray();
    }

    private sealed class CancellationProbeStream : Stream
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _read;

        internal Task Disposed => _disposed.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _read, 1) == 0)
            {
                buffer.Span[0] = (byte)'x';
                return 1;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed.TrySetResult();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            _disposed.TrySetResult();
            await base.DisposeAsync();
        }
    }

    [Test]
    public async Task NativeAndManagedCloseRequestsUseTheConfiguredGuard()
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new RecordingWindowHostFactory { SupportsCloseConfirmation = true };
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            WaitForConnection = false,
        });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            ConfirmCloseAsync = _ => { entered.TrySetResult(); return new(decision.Task); },
        });
        Assert.True(window.Capabilities.HasFlag(DesktopWindowCapabilities.CloseConfirmation));
        Assert.NotNull(factory.Host?.Options?.CloseRequested);
        factory.Host!.Options!.CloseRequested!();
        await entered.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
        var request = window.RequestCloseAsync();
        decision.SetResult(false);
        Assert.False(await request);
        Assert.True(window.IsOpen);
        decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        decision.SetResult(true);
        Assert.True(await window.RequestCloseAsync());
        Assert.False(window.IsOpen);
        Assert.False(factory.Host.IsOpen);
        await using var reopened = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
        Assert.False(window.IsOpen);
        Assert.Equal(0, window.NativeHandle);
        Assert.True(await window.RequestCloseAsync());
        Assert.True(reopened.IsOpen);
    }

    [Test]
    public async Task DocumentStartHostReceivesCredentialsWithheldFromFetchableScripts()
    {
        var factory = new RecordingWindowHostFactory { SupportsDocumentStartScript = true };
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory, WaitForConnection = false,
        });
        await using var surface = await host.CreateSurfaceAsync();
        using var client = new HttpClient();
        var fetched = await client.GetStringAsync(new Uri(surface.Url, "webui.js"));
        var token = ExtractUnsigned(fetched, "const TOKEN = ");
        var credential = ExtractQuoted(fetched, "const SESSION_CREDENTIAL = \"");

        await using (var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded }))
        {
            var injected = Assert.IsType<string>(factory.Host?.Options?.DocumentStartScript);
            Assert.Contains($"const admitted = [\"http://127.0.0.1:{surface.Url.Port}\"];", injected, StringComparison.Ordinal);
            Assert.Contains("if (!admitted.includes(globalThis.location.origin)) return;", injected, StringComparison.Ordinal);
            // SEC-05: a frame under a foreign document gets nothing.
            Assert.Contains("globalThis.location.ancestorOrigins", injected, StringComparison.Ordinal);
            Assert.Contains("try { top = globalThis.top.location.origin; } catch { return; }", injected, StringComparison.Ordinal);
            Assert.Contains($"token: {token},", injected, StringComparison.Ordinal);
            Assert.Contains($"sessionCredential: \"{credential}\"", injected, StringComparison.Ordinal);
            foreach (var path in new[] { "webui.js", "runic-desktop.js" })
            {
                var script = await client.GetStringAsync(new Uri(surface.Url, path));
                Assert.DoesNotContain(credential, script, StringComparison.Ordinal);
                Assert.DoesNotContain(token.ToString(System.Globalization.CultureInfo.InvariantCulture), script, StringComparison.Ordinal);
                Assert.Contains("globalThis[Symbol.for(\"runic-desktop.bootstrap\")]?.sessionCredential", script, StringComparison.Ordinal);
            }
            await window.CloseAsync();
        }

        Assert.Contains(credential, await client.GetStringAsync(new Uri(surface.Url, "webui.js")), StringComparison.Ordinal);
    }

    [Test]
    public async Task ClosedNativeWindowDoesNotRemainOpenBecauseItsSocketIsStillAuthenticated()
    {
        var factory = new RecordingWindowHostFactory { SupportsCloseConfirmation = true };
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory, WaitForConnection = false,
        });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded, ConfirmCloseAsync = _ => ValueTask.FromResult(true),
        });
        using var client = new HttpClient();
        var bootstrap = await client.GetStringAsync(new Uri(surface.Url, "webui.js"));
        var token = ExtractUnsigned(bootstrap, "const TOKEN = ");
        var credential = ExtractQuoted(bootstrap, "const SESSION_CREDENTIAL = \"");
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(5));
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", $"{surface.Url.Scheme}://{surface.Url.Authority}");
        socket.Options.AddSubProtocol($"runic-desktop.{credential}");
        await socket.ConnectAsync(ToWebSocketUrl(surface.Url), timeout.Token);
        await SendPacketAsync(socket, CreatePacket(token, 0, 0xF5, [0]), timeout.Token);
        _ = await ReceivePacketAsync(socket, timeout.Token);
        Assert.True(await window.RequestCloseAsync(timeout.Token));
        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.False(window.IsOpen);
        Assert.Equal(0, window.NativeHandle);
        await window.WaitForCloseAsync(timeout.Token);
    }

    [Test]
    public async Task WaitForCloseAsyncReturnsAfterCloseAndHonoursCancellation()
    {
        var factory = new RecordingWindowHostFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory, WaitForConnection = false,
        });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });

        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => window.WaitForCloseAsync(cancelled.Token).AsTask());
        }
        Assert.True(window.IsOpen);

        var wait = window.WaitForCloseAsync().AsTask();
        Assert.False(wait.IsCompleted);
        await window.CloseAsync();
        await wait.WaitScaledAsync(TimeSpan.FromSeconds(5));
        Assert.False(window.IsOpen);
        Assert.False(factory.Host!.IsOpen);
    }

    [Test]
    public async Task CloseAsyncCancellationStopsOnlyTheCallersWait()
    {
        var factory = new RecordingWindowHostFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory, WaitForConnection = false,
        });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
        var native = factory.Host!;
        native.ReleaseClose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();

        var cancelledClose = window.CloseAsync(caller.Token).AsTask();
        await native.CloseEntered.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
        var sharedClose = window.CloseAsync().AsTask();
        try
        {
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledClose);
            Assert.False(sharedClose.IsCompleted);
            Assert.True(native.IsOpen);
        }
        finally { native.ReleaseClose.TrySetResult(); }

        await sharedClose.WaitScaledAsync(TimeSpan.FromSeconds(5));
        Assert.False(native.IsOpen);
        Assert.False(window.IsOpen);
        await window.WaitForCloseAsync().AsTask().WaitScaledAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task EmbeddedWindowAcceptsNegativeCoordinates()
    {
        var factory = new RecordingWindowHostFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory, WaitForConnection = false,
        });
        var options = new DesktopWindowOptions { Browser = BrowserKind.Embedded, X = -1280, Y = -40 };
        Assert.DoesNotContain(host.Validate(options).Diagnostics,
            static item => item.Option is "DesktopWindowOptions.X" or "DesktopWindowOptions.Y");
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(options);

        Assert.Equal(-1280, factory.Host!.Options!.X);
        Assert.Equal(-40, factory.Host.Options.Y);
        Assert.True(window.Capabilities.HasFlag(DesktopWindowCapabilities.Move));
        await window.MoveAsync(-300, -20);
        Assert.Equal((-300, -20), factory.Host.Position);
    }

    [Test]
    public async Task ForcedCloseBypassesPendingConfirmationAndNewWindowHasNoStaleGuard()
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new RecordingWindowHostFactory { SupportsCloseConfirmation = true };
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            WaitForConnection = false,
        });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            ConfirmCloseAsync = _ => { entered.TrySetResult(); return new(decision.Task); },
        });
        var request = window.RequestCloseAsync();
        await entered.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
        await window.CloseAsync();
        Assert.False(await request);
        await using var reopened = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
        decision.SetResult(true);
        Assert.Null(factory.Host?.Options?.CloseRequested);
        Assert.True(reopened.IsOpen);
        Assert.True(await reopened.RequestCloseAsync());
        Assert.False(reopened.IsOpen);
        using var client = new HttpClient();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync(new Uri(surface.Url, "webui.js"))).StatusCode);
    }

    [Test]
    public async Task UnsupportedCustomHostCannotSilentlyIgnoreConfirmation()
    {
        var factory = new RecordingWindowHostFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            WaitForConnection = false,
        });
        await using var surface = await host.CreateSurfaceAsync();
        var error = await Assert.ThrowsAsync<DesktopException>(async () => await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            ConfirmCloseAsync = _ => ValueTask.FromResult(false),
        }));
        Assert.IsType<NotSupportedException>(error.InnerException);
        Assert.False(factory.Host!.IsOpen);
        Assert.Null(factory.Host.Options);
        await using var unguarded = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
        Assert.False(unguarded.Capabilities.HasFlag(DesktopWindowCapabilities.CloseConfirmation));
    }

    [Test]
    public async Task BrowserPresentationCannotSilentlyIgnoreConfirmation()
    {
        var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            DiagnosticSink = diagnostics.Enqueue,
            WaitForConnection = false,
        });
        await using var surface = await host.CreateSurfaceAsync();

        var error = await Assert.ThrowsAsync<DesktopException>(async () => await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Any,
            ConfirmCloseAsync = _ => ValueTask.FromResult(false),
        }));

        Assert.Equal("close-confirmation-unavailable", error.Code);
        Assert.Equal(DesktopErrorCategory.CapabilityDenied, error.Category);
        Assert.Contains(diagnostics, item => item.Code == error.Code && item.CorrelationId == error.CorrelationId);
        // The embedded window with an explicit browser fallback is valid; only the fallback opens unconfirmed.
        DesktopSurface.ValidateWindowOptions(new DesktopWindowOptions
        {
            PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
            ConfirmCloseAsync = _ => ValueTask.FromResult(false),
        });
    }

    private sealed class RecordingWindowHostFactory : IDesktopWindowHostFactory
    {
        internal RecordingWindowHost? Host { get; private set; }
        public bool IsSupported => true;
        public bool SupportsCloseConfirmation { get; init; }
        public bool SupportsDocumentStartScript { get; init; }
        public bool Announces { get; init; }
        public IDesktopWindowHost Create() => Host = new RecordingWindowHost
        {
            SupportsCloseConfirmation = SupportsCloseConfirmation,
            SupportsDocumentStartScript = SupportsDocumentStartScript,
            Announces = Announces,
        };
    }

    private sealed class RecordingWindowHost : IDesktopWindowHost
    {
        public bool SupportsCloseConfirmation { get; init; }
        public bool SupportsDocumentStartScript { get; init; }
        public bool Announces { get; init; }
        public DesktopWindowCapabilities Capabilities => DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.Focus |
            DesktopWindowCapabilities.Minimize | DesktopWindowCapabilities.Maximize | DesktopWindowCapabilities.Resize |
            DesktopWindowCapabilities.Move | (Announces ? DesktopWindowCapabilities.Announcements : DesktopWindowCapabilities.None);
        internal ConcurrentQueue<(string Message, DesktopAnnouncementPriority Priority)> Announcements { get; } = new();

        public ValueTask AnnounceAsync(string message, DesktopAnnouncementPriority priority, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Announcements.Enqueue((message, priority));
            return ValueTask.CompletedTask;
        }
        public event EventHandler? Closed;
        public bool IsOpen { get; private set; }
        public nint NativeHandle => IsOpen ? 1 : 0;
        internal Uri? Url { get; private set; }
        internal DesktopWindowHostOptions? Options { get; private set; }

        public ValueTask OpenAsync(
            Uri url,
            DesktopWindowHostOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Url = url;
            Options = options;
            IsOpen = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Url = url;
            return ValueTask.CompletedTask;
        }

        internal TaskCompletionSource CloseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? ReleaseClose { get; set; }
        internal (int X, int Y)? Position { get; private set; }

        public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloseEntered.TrySetResult();
            if (ReleaseClose is { } release) await release.Task;
            IsOpen = false;
            Closed?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask FocusAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
        public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default)
        {
            Position = (x, y);
            return ValueTask.CompletedTask;
        }
        internal ConcurrentQueue<bool> Visibility { get; } = new();
        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Visibility.Enqueue(visible);
            return ValueTask.CompletedTask;
        }
        // The user closes the native window.
        internal void CloseByUser()
        {
            IsOpen = false;
            Closed?.Invoke(this, EventArgs.Empty);
        }
        public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync()
        {
            IsOpen = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnavailableWindowHostFactory : IDesktopWindowHostFactory
    {
        public bool IsSupported => false;
        public IDesktopWindowHost Create() => throw new InvalidOperationException("An unavailable host cannot be created.");
    }
}
