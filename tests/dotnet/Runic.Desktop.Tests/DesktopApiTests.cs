using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Runic.Desktop.Tests;

public sealed class DesktopApiTests
{
    private const int PacketHeaderSize = 8;

    [Theory]
    [InlineData(true, false, true, null)] // Application.Run worker, AppKit loop active.
    [InlineData(true, false, false, "macos-main-thread-required")]
    [InlineData(true, true, false, null)] // Direct synchronous main-thread startup.
    [InlineData(true, true, true, null)]
    [InlineData(false, false, true, "webkit-runtime-missing")]
    [InlineData(false, true, false, "webkit-runtime-missing")]
    public void MacOsDiscoveryRequiresFrameworkAndAnAvailableMainThread(
        bool frameworkAvailable, bool isMainThread, bool eventLoopRunning, string? diagnosticCode)
    {
        var diagnostic = DesktopPlatform.GetMacOsEmbeddedDiagnostic(frameworkAvailable, isMainThread, eventLoopRunning);
        Assert.Equal(diagnosticCode, diagnostic?.Code);
        if (diagnosticCode == "macos-main-thread-required")
            Assert.Contains("DesktopEventLoop.Run()", diagnostic!.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxEmbeddedSelectionIsRequiredAndActionable()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var host = await DesktopHost.StartAsync();
        var availability = host.GetPresentationPreflight(new() { Browser = BrowserKind.Embedded });
        Assert.False(availability.IsAvailable);
        Assert.Equal("linux-embedded-backend-not-selected", availability.Diagnostic?.Code);
        await using var surface = await host.CreateSurfaceAsync();
        var error = await Assert.ThrowsAsync<DesktopException>(async () => await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded }));
        Assert.Equal("linux-embedded-backend-not-selected", error.Code);
    }

    [Fact]
    public async Task Gtk4SelectionRequiresItsOptionalProvider()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var host = await DesktopHost.StartAsync(new() { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk4WebKit6 } });
        Assert.Equal("gtk4-provider-missing", host.GetPresentationPreflight(new() { Browser = BrowserKind.Embedded }).Diagnostic?.Code);
    }

    [Fact]
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

    [Fact]
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

    [Fact]
    public void PublicApiContainsNoWebUiCompatibilityIdentity()
    {
        // The full surface is tracked by PublicAPI.Shipped.txt and PublicApiAnalyzers.
        Assert.DoesNotContain(
            typeof(DesktopHost).Assembly.ExportedTypes,
            static type => type.FullName!.Contains("WebUi", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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

    [Fact]
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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

    [Fact]
    public async Task HostSharesListenerAndCreatesARequestScopePerRequest()
    {
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            ConfigureServices = services => services.AddScoped<RequestMarker>(),
        });
        await using var first = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Path = "first",
            ContentHandler = static (request, _) => ValueTask.FromResult<ContentResponse?>(
                ContentResponse.Text($"first:{request.Services.GetRequiredService<RequestMarker>().Id}")),
        });
        await using var second = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Path = "second",
            ContentHandler = static (request, _) => ValueTask.FromResult<ContentResponse?>(
                ContentResponse.Text($"second:{request.Services.GetRequiredService<RequestMarker>().Id}")),
        });
        await using var isolated = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            UseIsolatedListener = true,
            Content = "isolated",
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

    [Fact]
    public async Task ContentRequestExposesAnImmutableCaseInsensitiveHeaderSnapshot()
    {
        IReadOnlyDictionary<string, string>? observed = null;
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            ContentHandler = (request, _) =>
            {
                observed = request.Headers;
                return ValueTask.FromResult<ContentResponse?>(ContentResponse.Text("ok"));
            },
        });
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("If-None-Match", "\"asset-v1\"");

        _ = await client.GetStringAsync(surface.Url);

        Assert.NotNull(observed);
        Assert.Equal("\"asset-v1\"", observed["if-none-match"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)observed).Add("x", "y"));
    }

    [Fact]
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
            Content = "original",
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

    [Fact]
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
        Assert.False(window.FellBack);
        Assert.Equal(BrowserKind.Embedded, window.Browser);
        Assert.Equal(BrowserKind.Embedded, window.RequestedBrowser);
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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
        Assert.Equal("custom-window-host-unavailable", requestedOnly.Diagnostic?.Code);
        Assert.Equal(DesktopPresentationPolicy.EmbeddedThenBrowser, explicitFallback.PresentationPolicy);
        Assert.NotNull(explicitFallback.Fallback);
        Assert.Equal(
            explicitFallback.Fallback!.IsAvailable && !explicitFallback.Preferred.IsAvailable,
            explicitFallback.WouldFallBack);
    }

    [Fact]
    public async Task PreflightRejectsUnknownPresentationPolicies()
    {
        await using var host = await DesktopHost.StartAsync();

        Assert.Throws<ArgumentOutOfRangeException>(() => host.GetPresentationPreflight(new DesktopWindowOptions
        {
            PresentationPolicy = (DesktopPresentationPolicy)42,
        }));
    }

    [Fact]
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

            Assert.True(window.FellBack);
            Assert.Equal(BrowserKind.Chrome, window.RequestedBrowser);
            Assert.Equal(BrowserKind.Chrome, window.Browser);
            Assert.Contains(diagnostics, static item => item.Code == "embedded-presentation-fallback");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EmbeddedConnectionTimeoutIsReportedInsteadOfFallingBack()
    {
        var factory = new RecordingWindowHostFactory();
        var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            DiagnosticSink = diagnostics.Enqueue,
            ConnectionTimeout = TimeSpan.FromMilliseconds(200),
        });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = "never connects",
        });

        var exception = await Assert.ThrowsAsync<DesktopException>(async () =>
            await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded,
                PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
            }));

        Assert.Equal(DesktopErrorCategory.TimedOut, exception.Category);
        Assert.Equal("presentation-connection-timeout", exception.Code);
        var timeout = Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Contains("no request reached the server", timeout.Message);
        Assert.Contains(diagnostics, static item => item.Code == "presentation-connection-timeout");
        Assert.DoesNotContain(diagnostics, static item => item.Code == "embedded-presentation-fallback");
        Assert.False(factory.Host?.IsOpen);
        // Only the presentation closed; the surface keeps serving for its owner.
        using var client = new HttpClient();
        Assert.Equal("never connects", await client.GetStringAsync(surface.Url));
    }

    [Fact]
    public async Task ConnectionTimeoutReportsHowFarThePageGot()
    {
        var factory = new RecordingWindowHostFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            ConnectionTimeout = TimeSpan.FromSeconds(2),
        });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = "no bridge script",
        });
        var opening = surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
        }).AsTask();
        using var client = new HttpClient();
        Assert.Equal("no bridge script", await client.GetStringAsync(surface.Url));

        var exception = await Assert.ThrowsAsync<DesktopException>(() => opening);

        var timeout = Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Contains("the page was requested, but not /webui.js", timeout.Message);
    }

    [Fact]
    public void AvailabilityReportsConcretePresentationsAndActionableFailures()
    {
        var availability = DesktopPlatform.GetAvailability(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.False(string.IsNullOrWhiteSpace(availability.Platform));
        Assert.Contains(availability.Presentations, static item => item.Browser == BrowserKind.Embedded);
        Assert.DoesNotContain(availability.Presentations, static item => item.Browser == BrowserKind.Any);
        Assert.All(
            availability.Diagnostics,
            static diagnostic => Assert.False(string.IsNullOrWhiteSpace(diagnostic.Remediation)));
    }

    [Fact]
    public async Task PublicStreamingResponseDoesNotOpenForHead()
    {
        var factoryCalls = 0;
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            ContentHandler = (request, _) => ValueTask.FromResult<ContentResponse?>(
                request.Path == "/stream"
                    ? ContentResponse.Stream(
                        _ =>
                        {
                            Interlocked.Increment(ref factoryCalls);
                            return ValueTask.FromResult<Stream>(new MemoryStream("stream"u8.ToArray()));
                        },
                        contentLength: 6)
                    : null),
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

    [Theory]
    [InlineData("http://127.0.0.1:4100/entry/index.html", true)]
    [InlineData("HTTP://127.0.0.1:4100/", true)]
    [InlineData("http://127.0.0.1:4101/", false)]
    [InlineData("http://localhost:4100/", false)]
    [InlineData("https://127.0.0.1:4100/", false)]
    [InlineData("about:blank", false)]
    [InlineData(null, false)]
    public void MediaCaptureGrantIsBoundToThePresentedOrigin(string? requester, bool expected) =>
        Assert.Equal(expected, Internal.PresentationSecurityPolicy.IsSameOrigin(new Uri("http://127.0.0.1:4100/surface/"), requester));

    [Fact]
    public async Task LocalContentDeniesMediaCaptureByDefault()
    {
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = "safe" });
        using var client = new HttpClient();

        using var response = await client.GetAsync(surface.Url);

        Assert.Equal("camera=(), microphone=()", response.Headers.GetValues("Permissions-Policy").Single());
    }

    [Fact]
    public async Task ClosingSurfaceReportsCauseAndReleasesPublicStream()
    {
        ContentRequest? observedRequest = null;
        var stream = new CancellationProbeStream();
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            ContentHandler = (request, _) =>
            {
                observedRequest = request;
                return ValueTask.FromResult<ContentResponse?>(
                    request.Path == "/stream"
                        ? ContentResponse.Stream(_ => ValueTask.FromResult<Stream>(stream))
                        : null);
            },
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
        await stream.Disposed.WaitAsync(TimeSpan.FromSeconds(5));
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

    [Fact]
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
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

    [Fact]
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
            Assert.Contains($"[\"http://127.0.0.1:{surface.Url.Port}\"].includes(globalThis.location.origin)", injected, StringComparison.Ordinal);
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

    [Fact]
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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
        window.WaitForClose(timeout.Token);
    }

    [Fact]
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
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

    [Fact]
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

    [Theory]
    [InlineData(BrowserKind.Any, (DesktopPresentationPolicy)0)]
    [InlineData(BrowserKind.Embedded, DesktopPresentationPolicy.EmbeddedThenBrowser)]
    public void BrowserPresentationCannotSilentlyIgnoreConfirmation(BrowserKind browser, DesktopPresentationPolicy policy)
    {
        Assert.Throws<ArgumentException>(() => DesktopSurface.ValidateWindowOptions(new DesktopWindowOptions
        {
            Browser = browser,
            PresentationPolicy = policy,
            ConfirmCloseAsync = _ => ValueTask.FromResult(false),
        }));
    }

    private sealed class RecordingWindowHostFactory : IDesktopWindowHostFactory
    {
        internal RecordingWindowHost? Host { get; private set; }
        public bool IsSupported => true;
        public bool SupportsCloseConfirmation { get; init; }
        public bool SupportsDocumentStartScript { get; init; }
        public IDesktopWindowHost Create() => Host = new RecordingWindowHost
        {
            SupportsCloseConfirmation = SupportsCloseConfirmation,
            SupportsDocumentStartScript = SupportsDocumentStartScript,
        };
    }

    private sealed class RecordingWindowHost : IDesktopWindowHost
    {
        public bool SupportsCloseConfirmation { get; init; }
        public bool SupportsDocumentStartScript { get; init; }
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

        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsOpen = false;
            Closed?.Invoke(this, EventArgs.Empty);
            return ValueTask.CompletedTask;
        }

        public ValueTask FocusAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(uint width, uint height, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
        public ValueTask MoveAsync(uint x, uint y, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
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
