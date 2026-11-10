using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Platform;

namespace Runic.Application.Testing;

/// <summary>
/// Plays the host of the Runic host protocol (<c>runic-host/1</c>) for a guest application in a test, without a native
/// window: it starts the guest in hosted mode, completes the handshake, answers each Window's <c>window-open</c> and
/// records its presentation requests, and exposes each Window's one-time launch address for a browser test.
/// </summary>
/// <remarks>
/// <code>
/// var start = RunicServeOnlyApplication.CreateStartInfo("path/to/Tool.dll");
/// start.ArgumentList.Add("hosted");                    // The parameter the guest defines for hosted mode.
/// await using var host = await RunicTestHost.StartGuestAsync(start);
/// var window = await host.WaitForWindowAsync("MainWindow", TimeSpan.FromSeconds(30));
/// await page.GotoAsync(window.LaunchUrl.ToString());    // Playwright loads the guest's page as the host's window would.
/// Assert.True(await window.RequestCloseAsync());
/// Assert.Equal(0, await host.StopAsync());
/// </code>
/// The guest must reach hosted mode through the parameter it defines, so the start information names it. The test host
/// writes the offer to the guest's standard input, keeps standard input open while it runs and reads the channel line
/// from standard output; the guest's other output and its standard error are captured for failure messages. It
/// connects again when its connection closes before the <c>hello</c> reply, within the start timeout, answers only the
/// base protocol's requests, and lists a window once it answered its <c>window-open</c>.
/// <see cref="StopAsync"/> sends <c>shutdown</c> and waits for the guest to exit; disposing closes standard input and the
/// channel, as a host that went away does, and kills the guest's process tree when it has not exited in time. On
/// Windows the guest runs in a job object that kills it when the test process ends.
/// The protocol and this type are experimental; see <c>docs/experimental.md</c>.
/// </remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed class RunicTestHost : IAsyncDisposable
{
    private static readonly TimeSpan s_killTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_outputDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly RunicServeOnlyApplication.WindowsJob? _job;
    private readonly RunicTestHostOptions _options;
    private readonly StringBuilder _output = new();
    private readonly StringBuilder _errors = new();
    private readonly TaskCompletionSource<string?> _launchLine = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _reading;
    // The windows reported in Windows, once their window-open was answered; _known has them from the request on, so a
    // window-closed right after the reply finds its window.
    private readonly List<RunicTestHostedWindow> _windows = [];
    private readonly Dictionary<string, RunicTestHostedWindow> _known = new(StringComparer.Ordinal);
    private TaskCompletionSource _windowsChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HostChannel? _channel;
    private int _disposed;

    private RunicTestHost(Process process, RunicServeOnlyApplication.WindowsJob? job, RunicTestHostOptions options)
    {
        _process = process;
        _job = job;
        _options = options;
        _reading = Task.WhenAll(ReadOutputAsync(), ReadErrorsAsync());
        _ = WatchExitAsync();
    }

    /// <summary>The guest application as its <c>hello</c> reply named it.</summary>
    public RunicTestGuest Guest { get; private set; } = null!;

    /// <summary>The protocol features in use on the channel; the base protocol has none.</summary>
    public IReadOnlyList<string> Features { get; private set; } = [];

    /// <summary>The guest's process id.</summary>
    public int ProcessId => _process.Id;

    /// <summary>Completes with the guest's exit code when its process exited.</summary>
    public Task<int> Exited => _exited.Task;

    /// <summary>The guest's standard output so far, the channel line excepted.</summary>
    public string StandardOutput
    {
        get { lock (_output) return _output.ToString(); }
    }

    /// <summary>The guest's standard error so far.</summary>
    public string StandardError
    {
        get { lock (_errors) return _errors.ToString(); }
    }

    /// <summary>
    /// Every window the guest asked for, in order, refused and closed ones included. A window is listed once its
    /// <c>window-open</c> was answered.
    /// </summary>
    public IReadOnlyList<RunicTestHostedWindow> Windows
    {
        get { lock (_windows) return [.. _windows]; }
    }

    /// <summary>Starts a guest application in hosted mode and completes the handshake.</summary>
    /// <param name="startInfo">
    /// How to start the guest, with the arguments that select its hosted mode, such as
    /// <see cref="RunicServeOnlyApplication.CreateStartInfo"/> plus <c>hosted</c>. It is not changed: the guest starts
    /// with redirected standard streams and without <c>RUNIC_APPLICATION_SERVE_ONLY</c> and
    /// <c>RUNIC_APPLICATION_CLOSE_AFTER_OPEN</c>.
    /// </param>
    /// <param name="options">The host's name, protocols, timeouts and answers; <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the start; the guest is then killed.</param>
    /// <returns>The running guest. Dispose it to end it.</returns>
    /// <exception cref="Runic.Application.Views.RunicHostedApplicationException">
    /// The guest could not be hosted: <c>guest-start-timeout</c>, <c>guest-exited</c>, <c>guest-not-hostable</c>,
    /// <c>guest-refused</c> or <c>protocol-unsupported</c>. The guest was killed; the message has its output.
    /// </exception>
    public static async Task<RunicTestHost> StartGuestAsync(ProcessStartInfo startInfo, RunicTestHostOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        options ??= new RunicTestHostOptions();
        var job = OperatingSystem.IsWindows() ? RunicServeOnlyApplication.WindowsJob.TryCreate() : null;
        Process process;
        try
        {
            process = Process.Start(GuestStartInfo(startInfo))
                ?? throw new InvalidOperationException($"The guest '{startInfo.FileName}' did not start.");
        }
        catch
        {
            job?.Dispose();
            throw;
        }
        job?.TryAssign(process);
        var host = new RunicTestHost(process, job, options);
        try
        {
            await host.HandshakeAsync(cancellationToken).ConfigureAwait(false);
            return host;
        }
        catch
        {
            await host.KillAsync().ConfigureAwait(false);
            host.Release();
            throw;
        }
    }

    // A copy of the caller's start information for hosted mode (host-protocol.md, Starting the guest).
    private static ProcessStartInfo GuestStartInfo(ProcessStartInfo startInfo)
    {
        var start = new ProcessStartInfo(startInfo.FileName)
        {
            WorkingDirectory = startInfo.WorkingDirectory,
            CreateNoWindow = startInfo.CreateNoWindow,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (startInfo.ArgumentList.Count > 0)
            foreach (var argument in startInfo.ArgumentList) start.ArgumentList.Add(argument);
        else start.Arguments = startInfo.Arguments;
        start.Environment.Clear();
        foreach (var (name, value) in startInfo.Environment) start.Environment[name] = value;
        start.Environment.Remove("RUNIC_APPLICATION_SERVE_ONLY");
        start.Environment.Remove("RUNIC_APPLICATION_CLOSE_AFTER_OPEN");
        return start;
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.StartTimeout);
        var nonce = RandomNumberGenerator.GetBytes(32);
        try
        {
            await _process.StandardInput.WriteAsync(HostProtocol.Line(HostProtocol.OfferPrefix,
                HostProtocol.Offer(_options.Protocols, HostProtocol.Base64Url(nonce), _options.HostName, _options.HostVersion))).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (IOException error) { throw Failure("guest-exited", "The guest closed its standard input before the offer.", error); }

        string? line;
        try { line = await _launchLine.Task.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("guest-start-timeout", $"The guest wrote no {HostProtocol.ChannelPrefix} line within {_options.StartTimeout}.");
        }
        // The launch line and the handshake with the host's own rules (HostLaunch): the refusal, the channel line, the
        // retried connection, the guest proof first, then ok and the protocol.
        var channel = HostLaunch.Channel(line, _options.Protocols, ExitCodeText, Failure);
        var hello = await HostLaunch.HandshakeAsync(channel, nonce, _options.HostName, _options.HostVersion, new JsonObject
        {
            ["features"] = new JsonArray(),
            ["locale"] = null,
            ["languages"] = new JsonArray(),
            ["appearance"] = null,
        }, Failure, () => _exited.Task.IsCompleted, ExitCodeText, deadline.Token, cancellationToken).ConfigureAwait(false);
        var socket = hello.Socket;
        Guest = new RunicTestGuest(hello.Name, hello.Version, hello.ApplicationId);
        Features = [];
        _channel = new HostChannel(socket, firstRequestId: 2, OnRequestAsync, OnPush);
        _channel.Start();
    }

    private Views.RunicHostedApplicationException Failure(string code, string message, Exception? inner = null, string? guestCode = null) =>
        new(code, Describe(message), inner) { GuestCode = guestCode };

    /// <summary>Waits for the first window of the Window type <paramref name="windowType"/>.</summary>
    /// <param name="windowType">
    /// The Window class's full name, or its name without the namespace, as <see cref="RunicServeOnlyApplication.WaitForWindowUrlAsync(string, TimeSpan, CancellationToken)"/> takes it.
    /// </param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    /// The window, opened or refused. Its <c>window-open</c> was answered: an opened window's <see cref="RunicTestHostedWindow.Opened"/>
    /// has completed.
    /// </returns>
    /// <exception cref="TimeoutException">The guest asked for no such window in time.</exception>
    public Task<RunicTestHostedWindow> WaitForWindowAsync(string windowType, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        WaitForNthWindowAsync(windowType, 0, timeout, cancellationToken);

    /// <summary>
    /// Waits for the window of the Window type <paramref name="windowType"/> that the guest asked for after
    /// <paramref name="index"/> others of that type, such as the second <c>DocumentWindow</c> with index 1.
    /// </summary>
    /// <param name="windowType">The Window class's full name, or its name without the namespace.</param>
    /// <param name="index">How many windows of that type come before it, from 0.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The window, opened or refused, once its <c>window-open</c> was answered.</returns>
    /// <exception cref="TimeoutException">The guest asked for no such window in time.</exception>
    public Task<RunicTestHostedWindow> WaitForNthWindowAsync(string windowType, int index, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowType);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return WaitForWindowAsync(windows => windows.Where(window => RunicServeOnlyApplication.IsType(window.Type, windowType)).Skip(index).FirstOrDefault(),
            index == 0 ? $"a window of type '{windowType}'" : $"window {index.ToString(CultureInfo.InvariantCulture)} of type '{windowType}'",
            timeout, cancellationToken);
    }

    /// <summary>Waits for the first window that <paramref name="match"/> accepts, in the order the guest asked for them.</summary>
    /// <param name="match">Decides about each window, such as <c>window =&gt; window.OwnerId == main.Id</c>.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The window, opened or refused, once its <c>window-open</c> was answered.</returns>
    /// <exception cref="TimeoutException">The guest asked for no such window in time.</exception>
    public Task<RunicTestHostedWindow> WaitForMatchingWindowAsync(Func<RunicTestHostedWindow, bool> match, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(match);
        return WaitForWindowAsync(windows => windows.FirstOrDefault(match), "a matching window", timeout, cancellationToken);
    }

    /// <summary>
    /// Reports a change of the host's appearance to the guest, as the <c>appearance</c> push does. A guest of this release
    /// does not follow it yet and keeps its own appearance.
    /// </summary>
    /// <param name="appearance">The host's appearance; <see langword="null"/> reports every member unknown.</param>
    /// <param name="cancellationToken">Cancels sending.</param>
    /// <returns>A task that completes when the push was sent.</returns>
    public Task SetAppearanceAsync(DesktopAppearance? appearance, CancellationToken cancellationToken = default) =>
        PushAsync(JsonNode.Parse(Views.BridgeDispatcher.AppearancePush(appearance))!.AsObject(), cancellationToken);

    private async Task<RunicTestHostedWindow> WaitForWindowAsync(Func<List<RunicTestHostedWindow>, RunicTestHostedWindow?> find,
        string what, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(timeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (true)
        {
            Task changed;
            lock (_windows)
            {
                if (find(_windows) is { } window) return window;
                changed = _windowsChanged.Task;
            }
            try { await changed.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(Describe($"The guest asked for no {what} within {timeout}."));
            }
        }
    }

    /// <summary>
    /// Ends the host session as a host does when it stops a guest: sends <c>shutdown</c>, waits for the guest to exit
    /// and kills its process tree when it has not exited within <paramref name="timeout"/>.
    /// </summary>
    /// <param name="timeout">How long to wait for the exit; the default is 10 seconds.</param>
    /// <param name="cancellationToken">Cancels the wait; the guest is then killed.</param>
    /// <returns>The guest's exit code.</returns>
    /// <exception cref="TimeoutException">The guest did not exit in time and was killed.</exception>
    public async Task<int> StopAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (_channel is { IsOpen: true } channel)
        {
            try { _ = await channel.RequestAsync(new JsonObject { ["id"] = 0, ["kind"] = "shutdown", ["reason"] = "stop" }, cancellationToken).ConfigureAwait(false); }
            catch (HostChannelClosedException) { }
            catch (OperationCanceledException)
            {
                await KillAsync().ConfigureAwait(false);
                throw;
            }
        }
        return await WaitForExitAsync(timeout ?? HostProtocol.DefaultStopTimeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try { return await _exited.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            await KillAsync().ConfigureAwait(false);
            throw new TimeoutException(Describe($"The guest did not exit within {timeout}; it was killed."));
        }
        catch (OperationCanceledException)
        {
            await KillAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Goes away as a host that exits does: closes the guest's standard input and the channel, waits up to 10 seconds
    /// for the guest to exit, and then kills its process tree.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            try { _process.StandardInput.Close(); }
            catch (Exception error) when (error is IOException or InvalidOperationException) { }
            if (_channel is { } channel) await channel.CloseAsync(WebSocketCloseStatus.NormalClosure, "").ConfigureAwait(false);
            try { _ = await WaitForExitAsync(HostProtocol.HostGoneExitTimeout + TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        finally { Release(); }
    }

    private void Release()
    {
        _job?.Dispose();
        _process.Dispose();
    }

    internal Task<JsonObject> RequestAsync(JsonObject request, CancellationToken cancellationToken) =>
        (_channel ?? throw new InvalidOperationException("The channel is not open.")).RequestAsync(request, cancellationToken);

    internal Task PushAsync(JsonObject push, CancellationToken cancellationToken) =>
        (_channel ?? throw new InvalidOperationException("The channel is not open.")).PushAsync(push, cancellationToken);

    // The base protocol's requests a guest sends for a window, with the window-open feature each needs, or none
    // (host-protocol.md, Presentation). Every other kind is not one of this protocol here: no feature is in use.
    private static readonly Dictionary<string, Views.RunicWindowPresentationFeatures> s_presentationRequests = new(StringComparer.Ordinal)
    {
        ["present"] = Views.RunicWindowPresentationFeatures.Present,
        ["minimize"] = Views.RunicWindowPresentationFeatures.Minimize,
        ["set-visible"] = Views.RunicWindowPresentationFeatures.Visibility,
        ["set-full-screen"] = Views.RunicWindowPresentationFeatures.FullScreen,
        ["set-document-edited"] = Views.RunicWindowPresentationFeatures.DocumentEdited,
        ["set-color-scheme"] = Views.RunicWindowPresentationFeatures.ColorScheme,
        ["toggle-maximized"] = Views.RunicWindowPresentationFeatures.None,
        ["resize"] = Views.RunicWindowPresentationFeatures.None,
        ["move"] = Views.RunicWindowPresentationFeatures.None,
        ["set-represented-file"] = Views.RunicWindowPresentationFeatures.None,
        ["set-layout-direction"] = Views.RunicWindowPresentationFeatures.None,
        ["announce"] = Views.RunicWindowPresentationFeatures.None,
    };

    private async Task OnRequestAsync(HostRequest request)
    {
        var id = HostProtocol.String(request.Message, "window");
        if (request.Kind == "window-open")
        {
            await OpenAsync(request, id).ConfigureAwait(false);
            return;
        }
        if (!s_presentationRequests.TryGetValue(request.Kind, out var feature))
        {
            await request.FailAsync("rejected", "invalid-request", $"'{request.Kind}' is not a request of the base protocol.").ConfigureAwait(false);
            return;
        }
        if (FindWindow(id) is not { Refused: false } target)
        {
            await request.FailAsync("rejected", "invalid-request", "The request names no window of this channel.").ConfigureAwait(false);
            return;
        }
        var presentation = new RunicTestPresentationRequest(target.Id, request.Kind, JsonDocument.Parse(request.Message.ToJsonString()).RootElement.Clone());
        target.Record(presentation);
        if (target.IsClosed)
        {
            await request.ReplyAsync(new JsonObject { ["outcome"] = "unavailable", ["reason"] = "owner-closed" }).ConfigureAwait(false);
            return;
        }
        // A request whose feature the window-open reply did not list (host-protocol.md, Presentation).
        if (!target.Features.HasFlag(feature))
        {
            await request.ReplyAsync(new JsonObject { ["outcome"] = "unavailable", ["reason"] = "backend-unavailable" }).ConfigureAwait(false);
            return;
        }
        var outcome = _options.Presentation?.Invoke(presentation) ?? new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        await request.ReplyAsync(outcome switch
        {
            PlatformResult<PlatformUnit>.Unavailable unavailable =>
                new JsonObject { ["outcome"] = "unavailable", ["reason"] = HostProtocol.Kebab(unavailable.Reason.ToString()) },
            PlatformResult<PlatformUnit>.Failed failed =>
                new JsonObject { ["outcome"] = "failed", ["code"] = HostProtocol.Kebab(failed.Code.ToString()) },
            _ => new JsonObject { ["outcome"] = "success" },
        }).ConfigureAwait(false);
    }

    // window-open: the window is listed in Windows after the reply was sent, so a test that found it may go away at once
    // without the guest missing the reply.
    private async Task OpenAsync(HostRequest request, string? id)
    {
        if (!HostProtocol.IsIdentifier(id) || FindWindow(id) is not null ||
            HostProtocol.String(request.Message, "url") is not { } text || !Uri.TryCreate(text, UriKind.Absolute, out var url) ||
            !url.IsLoopback || url.Scheme != Uri.UriSchemeHttp || !url.AbsolutePath.Contains("/_runic_launch/", StringComparison.Ordinal))
        {
            await request.FailAsync("rejected", "invalid-request", "The window-open is not valid.").ConfigureAwait(false);
            return;
        }
        var owner = HostProtocol.String(request.Message, "owner");
        if (owner is not null && FindWindow(owner) is not { IsClosed: false })
        {
            await request.FailAsync("rejected", "invalid-request", "The owner is not an open window of this guest.").ConfigureAwait(false);
            return;
        }
        var window = new RunicTestHostedWindow(this, id!, HostProtocol.String(request.Message, "type") ?? "", url,
            (int?)HostProtocol.Integer(request.Message, "width"), (int?)HostProtocol.Integer(request.Message, "height"), owner,
            HostProtocol.String(request.Message, "modality") == "window", HostProtocol.Boolean(request.Message, "guardClose") == true,
            _options.WindowFeatures);
        var allowed = _options.OpenWindow?.Invoke(window) ?? true;
        window.Refused = !allowed;
        lock (_windows) _known[window.Id] = window;
        try
        {
            if (!allowed)
            {
                window.MarkClosed();
                await request.FailAsync("rejected", "refused", "The test host's policy refused the window.").ConfigureAwait(false);
                return;
            }
            await request.ReplyAsync(new JsonObject
            {
                ["features"] = new JsonArray([.. FeatureNames(_options.WindowFeatures).Select(static name => (JsonNode)name)]),
                ["activity"] = new JsonObject { ["active"] = true, ["minimized"] = false, ["visible"] = true },
                ["colorScheme"] = "no-preference",
            }).ConfigureAwait(false);
            window.MarkOpened();
        }
        finally
        {
            if (!window.Opened.IsCompleted) window.MarkNotOpened();
            lock (_windows)
            {
                _windows.Add(window);
                (_windowsChanged, var changed) = (new(TaskCreationOptions.RunContinuationsAsynchronously), _windowsChanged);
                changed.TrySetResult();
            }
        }
    }

    private void OnPush(JsonObject push)
    {
        if (HostProtocol.String(push, "kind") == "window-closed" && FindWindow(HostProtocol.String(push, "window")) is { } window)
            window.MarkClosed();
    }

    private RunicTestHostedWindow? FindWindow(string? id)
    {
        if (id is null) return null;
        lock (_windows) return _known.GetValueOrDefault(id);
    }

    private static IEnumerable<string> FeatureNames(Views.RunicWindowPresentationFeatures features)
    {
        if (features.HasFlag(Views.RunicWindowPresentationFeatures.Present)) yield return "present";
        if (features.HasFlag(Views.RunicWindowPresentationFeatures.Minimize)) yield return "minimize";
        if (features.HasFlag(Views.RunicWindowPresentationFeatures.Visibility)) yield return "visibility";
        if (features.HasFlag(Views.RunicWindowPresentationFeatures.FullScreen)) yield return "full-screen";
        if (features.HasFlag(Views.RunicWindowPresentationFeatures.DocumentEdited)) yield return "document-edited";
        if (features.HasFlag(Views.RunicWindowPresentationFeatures.ColorScheme)) yield return "color-scheme";
        if (features.HasFlag(Views.RunicWindowPresentationFeatures.PerWindowColorScheme)) yield return "per-window-color-scheme";
        if (features.HasFlag(Views.RunicWindowPresentationFeatures.Activity)) yield return "activity";
        if (features.HasFlag(Views.RunicWindowPresentationFeatures.OwnedWindows)) yield return "owned-windows";
    }

    private async Task WatchExitAsync()
    {
        try { await _process.WaitForExitAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        try { await _reading.WaitAsync(s_outputDrainTimeout).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or IOException or ObjectDisposedException or InvalidOperationException) { }
        int code;
        try { code = _process.ExitCode; }
        catch (InvalidOperationException) { code = -1; }
        _exited.TrySetResult(code);
    }

    private async Task KillAsync()
    {
        try { _process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or AggregateException or Win32Exception or NotSupportedException) { }
        try { await _exited.Task.WaitAsync(s_killTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    private async Task ReadOutputAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                // The first launch line decides the start; it carries the channel key, so it is not kept.
                if (!_launchLine.Task.IsCompleted &&
                    (line.StartsWith(HostProtocol.ChannelPrefix, StringComparison.Ordinal) || line.StartsWith(HostProtocol.RefusedPrefix, StringComparison.Ordinal)))
                {
                    if (line.StartsWith(HostProtocol.RefusedPrefix, StringComparison.Ordinal)) lock (_output) _output.AppendLine(line);
                    _launchLine.TrySetResult(line);
                    continue;
                }
                lock (_output) _output.AppendLine(line);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally { _launchLine.TrySetResult(null); }
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                lock (_errors) _errors.AppendLine(line);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private string ExitCodeText()
    {
        try { return _process.HasExited ? _process.ExitCode.ToString(CultureInfo.InvariantCulture) : "no exit code yet"; }
        catch (InvalidOperationException) { return "an unknown code"; }
    }

    private string Describe(string message) => $"{message}\nStandard output:\n{StandardOutput}\nStandard error:\n{StandardError}";

    internal static string DefaultHostVersion =>
        typeof(RunicTestHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } version
            ? version.Split('+')[0]
            : "0.0.0";
}

/// <summary>The options of a <see cref="RunicTestHost"/>.</summary>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record RunicTestHostOptions
{
    /// <summary>The host's name in the offer; <c>Runic.Application.Testing</c> by default.</summary>
    public string HostName { get; init; } = "Runic.Application.Testing";

    /// <summary>The host's version in the offer; this package's version by default.</summary>
    public string HostVersion { get; init; } = RunicTestHost.DefaultHostVersion;

    /// <summary>The protocol versions the offer lists; <c>[1]</c> by default. Other values test a guest's refusal.</summary>
    public IReadOnlyList<int> Protocols { get; init; } = [HostProtocol.Version];

    /// <summary>How long the guest may take to write its channel line and answer the <c>hello</c>; 30 seconds by default.</summary>
    public TimeSpan StartTimeout { get; init; } = HostProtocol.DefaultStartTimeout;

    /// <summary>
    /// What each window can do, as the <c>window-open</c> reply lists it: every presentation feature but
    /// <see cref="Views.RunicWindowPresentationFeatures.PerWindowColorScheme"/> by default.
    /// </summary>
    public Views.RunicWindowPresentationFeatures WindowFeatures { get; init; } =
        Views.RunicWindowPresentationFeatures.Present | Views.RunicWindowPresentationFeatures.Minimize |
        Views.RunicWindowPresentationFeatures.Visibility | Views.RunicWindowPresentationFeatures.FullScreen |
        Views.RunicWindowPresentationFeatures.DocumentEdited | Views.RunicWindowPresentationFeatures.ColorScheme |
        Views.RunicWindowPresentationFeatures.Activity | Views.RunicWindowPresentationFeatures.OwnedWindows;

    /// <summary>
    /// Decides each <c>window-open</c>: <see langword="false"/> refuses the window, as a host's policy does.
    /// <see langword="null"/>, the default, allows every window.
    /// </summary>
    public Func<RunicTestHostedWindow, bool>? OpenWindow { get; init; }

    /// <summary>
    /// Answers each presentation request with a platform outcome; <see langword="null"/>, the default, answers success.
    /// </summary>
    public Func<RunicTestPresentationRequest, PlatformResult<PlatformUnit>>? Presentation { get; init; }
}

/// <summary>The guest application as its <c>hello</c> reply names it.</summary>
/// <param name="Name">The application's name.</param>
/// <param name="Version">The application's version.</param>
/// <param name="ApplicationId">The application's identity, or <see langword="null"/>.</param>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record RunicTestGuest(string Name, string Version, string? ApplicationId);

/// <summary>A presentation request a guest sent for one of its windows, such as <c>present</c> or <c>set-full-screen</c>.</summary>
/// <param name="WindowId">The window's id.</param>
/// <param name="Kind">The request's kind.</param>
/// <param name="Message">The whole request, with its members such as <c>fullScreen</c>.</param>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record RunicTestPresentationRequest(string WindowId, string Kind, JsonElement Message);

/// <summary>A window a guest asked a <see cref="RunicTestHost"/> for.</summary>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed class RunicTestHostedWindow
{
    private readonly RunicTestHost _host;
    private readonly List<RunicTestPresentationRequest> _requests = [];
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal RunicTestHostedWindow(RunicTestHost host, string id, string type, Uri launchUrl, int? width, int? height,
        string? ownerId, bool isModal, bool guardsClose, Views.RunicWindowPresentationFeatures features)
    {
        Features = features;
        _host = host;
        Id = id;
        Type = type;
        LaunchUrl = launchUrl;
        Width = width;
        Height = height;
        OwnerId = ownerId;
        IsModal = isModal;
        GuardsClose = guardsClose;
    }

    /// <summary>The guest's id for the window.</summary>
    public string Id { get; }

    /// <summary>The Window type, as the serve-only window line names it.</summary>
    public string Type { get; }

    /// <summary>
    /// The page's one-time launch address. The first request with it binds the guest's surface to that browser, as the
    /// host's window would be: open it in the browser under test, once.
    /// </summary>
    public Uri LaunchUrl { get; }

    /// <summary>The requested width of the window's content, if any.</summary>
    public int? Width { get; }

    /// <summary>The requested height of the window's content, if any.</summary>
    public int? Height { get; }

    /// <summary>The id of the guest's window that owns this one, or <see langword="null"/>.</summary>
    public string? OwnerId { get; }

    /// <summary>Whether the window is modal to its owner.</summary>
    public bool IsModal { get; }

    /// <summary>Whether the Window decides about its close, so a user close asks it first.</summary>
    public bool GuardsClose { get; }

    /// <summary>Whether <see cref="RunicTestHostOptions.OpenWindow"/> refused the window.</summary>
    public bool Refused { get; internal set; }

    /// <summary>
    /// What the window can do, as its <c>window-open</c> reply listed it (<see cref="RunicTestHostOptions.WindowFeatures"/>).
    /// A presentation request whose feature is missing is answered unavailable.
    /// </summary>
    public Views.RunicWindowPresentationFeatures Features { get; }

    /// <summary>
    /// Completes when the test host sent the successful <c>window-open</c> reply, after which the guest treats the window
    /// as open; cancelled when the window was refused. A window that <see cref="RunicTestHost.Windows"/> lists has been
    /// answered.
    /// </summary>
    public Task Opened => _opened.Task;

    /// <summary>Whether the guest closed the window (<c>window-closed</c>), or it was refused.</summary>
    public bool IsClosed => _closed.Task.IsCompleted;

    /// <summary>Completes when the guest closed the window.</summary>
    public Task Closed => _closed.Task;

    /// <summary>The presentation requests the guest sent for the window, in order.</summary>
    public IReadOnlyList<RunicTestPresentationRequest> PresentationRequests
    {
        get { lock (_requests) return [.. _requests]; }
    }

    internal void Record(RunicTestPresentationRequest request)
    {
        lock (_requests) _requests.Add(request);
    }

    internal void MarkClosed() => _closed.TrySetResult();

    internal void MarkOpened() => _opened.TrySetResult();

    internal void MarkNotOpened() => _opened.TrySetCanceled();

    /// <summary>Closes the window as its user would: sends <c>close-request</c> and returns the guest's decision.</summary>
    /// <param name="force">Whether the host closes it whatever the guest decides.</param>
    /// <param name="cancellationToken">Cancels the wait for the decision.</param>
    /// <returns>Whether the guest allowed the close; it then closes the Window and <see cref="Closed"/> completes.</returns>
    public async Task<bool> RequestCloseAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var request = new JsonObject { ["id"] = 0, ["kind"] = "close-request", ["window"] = Id };
        if (force) request["force"] = true;
        var reply = await _host.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        return HostProtocol.Boolean(reply, "ok") == true && HostProtocol.Boolean(reply, "allow") == true;
    }

    /// <summary>Reports the window's activity to the guest, as the <c>activity</c> push does.</summary>
    /// <param name="activity">Whether the window is active, minimized and visible.</param>
    /// <param name="cancellationToken">Cancels sending.</param>
    /// <returns>A task that completes when the push was sent.</returns>
    public Task SetActivityAsync(Views.RunicWindowActivity activity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return _host.PushAsync(new JsonObject
        {
            ["kind"] = "activity",
            ["window"] = Id,
            ["active"] = activity.IsActive,
            ["minimized"] = activity.IsMinimized,
            ["visible"] = activity.IsVisible,
        }, cancellationToken);
    }

    /// <summary>
    /// Reports the window's colour scheme to the guest, as the <c>color-scheme</c> push does after the user or the host
    /// changed it.
    /// </summary>
    /// <param name="colorScheme">The window's colour scheme.</param>
    /// <param name="cancellationToken">Cancels sending.</param>
    /// <returns>A task that completes when the push was sent.</returns>
    public Task SetColorSchemeAsync(DesktopColorScheme colorScheme, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(colorScheme)) throw new ArgumentOutOfRangeException(nameof(colorScheme));
        return _host.PushAsync(new JsonObject
        {
            ["kind"] = "color-scheme",
            ["window"] = Id,
            ["colorScheme"] = colorScheme switch
            {
                DesktopColorScheme.Light => "light",
                DesktopColorScheme.Dark => "dark",
                _ => "no-preference",
            },
        }, cancellationToken);
    }
}
