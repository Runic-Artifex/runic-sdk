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
/// base protocol's requests, the menus of <c>menus/1</c> (<see cref="RunicTestHostedWindow.MenuBar"/>,
/// <see cref="RunicTestHostedWindow.InvokeMenuItemAsync"/>), the dialogs of <c>dialogs/1</c>, which
/// <see cref="RunicTestHostOptions.FileDialog"/>, <see cref="RunicTestHostOptions.MessageBox"/> and
/// <see cref="RunicTestHostOptions.ContextMenu"/> answer, and the window services of <c>window-services/1</c> with a
/// simulated <see cref="Clipboard"/>, <see cref="Monitors"/>, recorded launches
/// (<see cref="RunicTestHostedWindow.LaunchRequests"/>) and dropped files (<see cref="RunicTestHostedWindow.DropFilesAsync"/>),
/// and lists a window once it answered its <c>window-open</c>. The guest starts with
/// <see cref="RunicTestHostOptions.Locale"/> and <see cref="RunicTestHostOptions.Appearance"/>, which
/// <see cref="SetLocaleAsync"/> and <see cref="SetAppearanceAsync"/> change.
/// <see cref="StopAsync"/> sends <c>shutdown</c> and waits for the guest to exit; disposing closes standard input and the
/// channel, as a host that went away does, and kills the guest's process tree when it has not exited in time. On
/// Windows the guest runs in a job object that kills it when the test process ends.
/// The protocol and this type are experimental; see <c>docs/experimental.md</c>.
/// </remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed partial class RunicTestHost : IAsyncDisposable
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

    /// <summary>
    /// The protocol features in use on the channel: those of <see cref="RunicTestHostOptions.Features"/> that the guest's
    /// <c>hello</c> reply lists too, such as <c>menus/1</c> and <c>dialogs/1</c>.
    /// </summary>
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
                HostProtocol.Offer(_options.Protocols, HostProtocol.Base64Url(nonce), _options.HostName, _options.HostVersion,
                    _options.AllowElevatedGuest))).ConfigureAwait(false);
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
        CheckLocale(_options.Locale, _options.Languages);
        var hello = await HostLaunch.HandshakeAsync(channel, nonce, _options.HostName, _options.HostVersion, new JsonObject
        {
            ["features"] = new JsonArray([.. _options.Features.Select(static name => (JsonNode)name)]),
            ["locale"] = _options.Locale,
            ["languages"] = HostServices.WriteLanguages(_options.Languages),
            ["appearance"] = _options.Appearance is null ? null : HostServices.WriteAppearance(_options.Appearance),
        }, Failure, () => _exited.Task.IsCompleted, ExitCodeText, deadline.Token, cancellationToken).ConfigureAwait(false);
        var socket = hello.Socket;
        Guest = new RunicTestGuest(hello.Name, hello.Version, hello.ApplicationId);
        Features = hello.Features;
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
    /// Reports a change of the host's appearance to the guest, as the <c>appearance</c> push does: the guest's Windows show
    /// it, in their pages' appearance (<c>appearance/1</c>), instead of the guest process's own desktop settings.
    /// </summary>
    /// <param name="appearance">The host's appearance; <see langword="null"/> reports every member unknown.</param>
    /// <param name="cancellationToken">Cancels sending.</param>
    /// <returns>A task that completes when the push was sent.</returns>
    public Task SetAppearanceAsync(DesktopAppearance? appearance, CancellationToken cancellationToken = default) =>
        PushAsync(JsonNode.Parse(Views.BridgeDispatcher.AppearancePush(appearance))!.AsObject(), cancellationToken);

    private async Task<RunicTestHostedWindow> WaitForWindowAsync(Func<List<RunicTestHostedWindow>, RunicTestHostedWindow?> find,
        string what, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ThrowIfAnswerFailed();
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
    /// <exception cref="InvalidOperationException">
    /// An answer of <see cref="RunicTestHostOptions"/> failed, such as a file dialog's that selected a relative path; the
    /// guest was stopped first.
    /// </exception>
    public async Task<int> StopAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var code = await StopCoreAsync(timeout, cancellationToken).ConfigureAwait(false);
        ThrowIfAnswerFailed();
        return code;
    }

    private async Task<int> StopCoreAsync(TimeSpan? timeout, CancellationToken cancellationToken)
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
        ["maximize"] = Views.RunicWindowPresentationFeatures.Maximize,
        ["restore"] = Views.RunicWindowPresentationFeatures.Maximize,
        ["set-placement"] = Views.RunicWindowPresentationFeatures.Placement,
        // region/1: the page title of a region window, and a move of the focus out of a region.
        ["set-title"] = Views.RunicWindowPresentationFeatures.None,
        ["region-focus"] = Views.RunicWindowPresentationFeatures.None,
    };

    private async Task OnRequestAsync(HostRequest request)
    {
        var id = HostProtocol.String(request.Message, "window");
        if (request.Kind == "window-open")
        {
            await OpenAsync(request, id).ConfigureAwait(false);
            return;
        }
        if (await OnFeatureRequestAsync(request, id).ConfigureAwait(false)) return;
        if (await OnServiceRequestAsync(request, id).ConfigureAwait(false)) return;
        if (!s_presentationRequests.TryGetValue(request.Kind, out var feature))
        {
            await request.FailAsync("rejected", "invalid-request", $"'{request.Kind}' is not a request of the base protocol.").ConfigureAwait(false);
            return;
        }
        if (FindWindow(id) is not { Refused: false } target ||
            request.Kind is "set-title" or "region-focus" && !target.IsRegion)
        {
            await request.FailAsync("rejected", "invalid-request", "The request names no window of this channel that it applies to.").ConfigureAwait(false);
            return;
        }
        if (request.Kind == "set-placement" && HostProtocol.Placement(request.Message) is null)
        {
            await request.FailAsync("rejected", "invalid-request", "The 'set-placement' request is missing a member or has one out of range.").ConfigureAwait(false);
            return;
        }
        var presentation = new RunicTestPresentationRequest(target.Id, request.Kind, JsonDocument.Parse(request.Message.ToJsonString()).RootElement.Clone());
        target.Record(presentation);
        if (!target.IsClosed && RegionOutcome(target, request.Kind) is { } regionOutcome)
        {
            await request.ReplyAsync(regionOutcome).ConfigureAwait(false);
            return;
        }
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
            _options.WindowFeatures)
        {
            Locale = HostProtocol.String(request.Message, "locale"),
            AcceptsFileDrops = HostProtocol.Boolean(request.Message, "fileDrops") == true && Features.Contains(HostServices.Feature, StringComparer.Ordinal),
        };
        if (Embeds(window))
        {
            window.IsRegion = true;
            window.Features &= ~RegionlessFeatures;
        }
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
            await request.ReplyAsync(window.IsRegion ? RegionReply(window) : new JsonObject
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
        if (HostProtocol.String(push, "kind") == "cancel")
        {
            OnCancel(push);
            return;
        }
        if (HostProtocol.String(push, "kind") == "window-closed" && FindWindow(HostProtocol.String(push, "window")) is { } window)
            window.MarkClosed();
        if (HostProtocol.String(push, "kind") == "region-page") OnRegionPage(push);
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
        if (features.HasFlag(Views.RunicWindowPresentationFeatures.Maximize)) yield return "maximize";
        if (features.HasFlag(Views.RunicWindowPresentationFeatures.Placement)) yield return "placement";
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

    private string Describe(string message) =>
        $"{message}{(Volatile.Read(ref _answerFailure) is { } failure ? "\n" + failure.Message : "")}\nStandard output:\n{StandardOutput}\nStandard error:\n{StandardError}";

    // The first answer of RunicTestHostOptions that failed, which the next wait reports, as the guest saw only a failed
    // dialog.
    private Exception? _answerFailure;

    internal void RecordAnswerFailure(Exception failure) => Interlocked.CompareExchange(ref _answerFailure, failure, null);

    internal void ThrowIfAnswerFailed()
    {
        if (Volatile.Read(ref _answerFailure) is { } failure) throw new InvalidOperationException(failure.Message, failure.InnerException);
    }

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
    /// Whether the offer allows an elevated guest, as a host's <c>RunicGuestStartInfo.AllowElevatedGuest</c> does;
    /// <see langword="false"/> by default. With it the test host plays an elevated host for a guest that sets
    /// <c>RunicGuestOptions.AllowElevatedHost</c>.
    /// </summary>
    /// <remarks>
    /// The guest checks its parent, this test process, itself: an elevated guest is hosted only when the test process runs
    /// elevated as the same user, and refuses with <c>guest-elevated-host-not-elevated</c> otherwise. A guest that does not
    /// run elevated ignores the offer's opt-in. Unlike Runic's host, the test host does not check that it runs elevated
    /// itself, so it can also show that a guest ignores the opt-in when it does not run elevated.
    /// </remarks>
    public bool AllowElevatedGuest { get; init; }

    /// <summary>
    /// What each window can do, as the <c>window-open</c> reply lists it: every presentation feature but
    /// <see cref="Views.RunicWindowPresentationFeatures.PerWindowColorScheme"/> by default.
    /// </summary>
    public Views.RunicWindowPresentationFeatures WindowFeatures { get; init; } =
        Views.RunicWindowPresentationFeatures.Present | Views.RunicWindowPresentationFeatures.Minimize |
        Views.RunicWindowPresentationFeatures.Visibility | Views.RunicWindowPresentationFeatures.FullScreen |
        Views.RunicWindowPresentationFeatures.DocumentEdited | Views.RunicWindowPresentationFeatures.ColorScheme |
        Views.RunicWindowPresentationFeatures.Activity | Views.RunicWindowPresentationFeatures.OwnedWindows |
        Views.RunicWindowPresentationFeatures.Maximize | Views.RunicWindowPresentationFeatures.Placement;

    /// <summary>
    /// Decides each <c>window-open</c>: <see langword="false"/> refuses the window, as a host's policy does.
    /// <see langword="null"/>, the default, allows every window.
    /// </summary>
    public Func<RunicTestHostedWindow, bool>? OpenWindow { get; init; }

    /// <summary>
    /// Chooses the windows that show in a region of a host page instead of a window of their own (<c>region/1</c>),
    /// as a host's policy does: their <c>window-open</c> reply names <c>presentation: "region"</c>, and
    /// <see cref="RunicTestHostedWindow.AttachRegionAsync"/> attaches them. Needs <c>region/1</c> in
    /// <see cref="Features"/> and in the guest's <c>hello</c>. <see langword="null"/>, the default, shows every window in
    /// a window.
    /// </summary>
    public Func<RunicTestHostedWindow, bool>? EmbedWindow { get; init; }

    /// <summary>
    /// The appearance override of each region window from its open (<c>region-appearance/1</c>), as a host's
    /// <c>RunicHostedRegionOptions.Appearance</c> gives it: the guest merges it with <see cref="Appearance"/> for that
    /// window's pages only. Each known member replaces the host's value; an unknown one, such as
    /// <see cref="DesktopColorScheme.NoPreference"/> or <see langword="null"/>, keeps it. Needs <c>region-appearance/1</c>
    /// and <c>region/1</c> in <see cref="Features"/> and in the guest's <c>hello</c>. <see langword="null"/>, the default,
    /// or a <see langword="null"/> result opens the window without an override; <see cref="RunicTestHost.SetRegionAppearanceAsync"/>
    /// changes it later.
    /// </summary>
    public Func<RunicTestHostedWindow, DesktopAppearance?>? RegionAppearance { get; init; }

    /// <summary>
    /// Answers each presentation request with a platform outcome; <see langword="null"/>, the default, answers success.
    /// </summary>
    public Func<RunicTestPresentationRequest, PlatformResult<PlatformUnit>>? Presentation { get; init; }

    /// <summary>
    /// The protocol features the host's <c>hello</c> lists: <c>menus/1</c>, <c>dialogs/1</c> and <c>window-services/1</c>
    /// by default. A guest's menus, dialogs and window services of a feature that is not listed report
    /// <c>OwnerUnavailable</c>, as in serve-only mode.
    /// </summary>
    public IReadOnlyList<string> Features { get; init; } = ["menus/1", "dialogs/1", "window-services/1"];

    /// <summary>
    /// The host's application locale in its <c>hello</c>, which the guest's <c>IRunicApplicationLocale</c> follows unless
    /// the guest sets a locale of its own; <see langword="null"/>, the default, for none.
    /// </summary>
    public string? Locale { get; init; }

    /// <summary>The languages the host's user prefers, in its <c>hello</c>; empty by default.</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];

    /// <summary>
    /// The host's appearance in its <c>hello</c>, which the guest's Windows show; <see langword="null"/>, the default, for an
    /// unknown appearance.
    /// </summary>
    public DesktopAppearance? Appearance { get; init; }

    /// <summary>
    /// The host's monitors that the guest lists (<c>monitors</c>), the first one the window's: one primary 1920 × 1080
    /// monitor, <c>test-monitor</c>, by default. <see cref="RunicTestHost.SetMonitorsAsync"/> changes them.
    /// </summary>
    public IReadOnlyList<DesktopMonitor> Monitors { get; init; } =
    [
        new DesktopMonitor("test-monitor", "Test monitor")
        {
            Bounds = new DesktopRect(0, 0, 1920, 1080),
            WorkArea = new DesktopRect(0, 0, 1920, 1040),
            IsPrimary = true,
        },
    ];

    /// <summary>
    /// Answers each file or link the guest asks the host to open (<c>launch</c>) with a platform outcome;
    /// <see langword="null"/>, the default, answers success. Every request is recorded in
    /// <see cref="RunicTestHostedWindow.LaunchRequests"/>; nothing is opened.
    /// </summary>
    public Func<RunicTestLaunchRequest, PlatformResult<PlatformUnit>>? Launch { get; init; }

    /// <summary>
    /// Whether a clipboard read for a window that is not active is refused, as Runic's host does by default;
    /// <see langword="true"/> by default. Each window is active when it opens; <see cref="RunicTestHostedWindow.SetActivityAsync"/>
    /// changes it.
    /// </summary>
    /// <remarks>
    /// Runic's host is stricter: it also refuses clipboard writes and launches for a window the user is not working in,
    /// and treats a window the guest opened or presented itself as not the user's until the user switches to it
    /// (<c>RunicHostedServiceRequest.IsGuestActivated</c>). The test host does not
    /// model the user's activation; test those rules against Runic's host.
    /// </remarks>
    public bool ClipboardReadsOnlyActiveWindow { get; init; } = true;

    /// <summary>
    /// Answers each file dialog (<c>file-dialog</c>): <see cref="RunicTestDialogAnswer.Select"/> with the absolute paths the
    /// user selected, and the filter they chose, or another answer, such as <see cref="RunicTestDialogAnswer.Dismiss"/>
    /// or <see cref="RunicTestDialogAnswer.Refuse"/>. The token is cancelled when the guest gives the dialog up or closes
    /// the window. <see langword="null"/>, the default, dismisses every dialog.
    /// </summary>
    /// <remarks>
    /// The guest opens the paths itself, as with its own dialogs: an open dialog's files must exist. The request's typed
    /// members (<see cref="RunicTestDialogRequest.DialogKind"/>, <see cref="RunicTestDialogRequest.Title"/>,
    /// <see cref="RunicTestDialogRequest.SuggestedName"/> and others) describe the dialog. An answer that does not fit the
    /// request, or a callback that throws, fails the guest's dialog and the test host's next wait.
    /// </remarks>
    public Func<RunicTestDialogRequest, CancellationToken, Task<RunicTestDialogAnswer>>? FileDialog { get; init; }

    /// <summary>
    /// Answers each message box (<c>message-box</c>): <see cref="RunicTestDialogAnswer.Choose"/> with the id of the button
    /// the user chose, <see cref="RunicTestDialogAnswer.Dismiss"/> for a box closed without one, or another answer. The
    /// token is cancelled when the guest gives the box up or closes the window. <see langword="null"/>, the default,
    /// chooses the box's cancel button, or none.
    /// </summary>
    public Func<RunicTestDialogRequest, CancellationToken, Task<RunicTestDialogAnswer>>? MessageBox { get; init; }

    /// <summary>
    /// Answers each context menu (<c>show-context-menu</c>): <see cref="RunicTestDialogAnswer.Choose"/> with the id of the
    /// item the user chose, which <see cref="RunicTestHostedWindow.FindMenuItem(JsonElement, string[])"/> finds by its
    /// title, <see cref="RunicTestDialogAnswer.Dismiss"/>, or another answer. <see langword="null"/>, the default,
    /// dismisses every menu.
    /// </summary>
    public Func<RunicTestDialogRequest, CancellationToken, Task<RunicTestDialogAnswer>>? ContextMenu { get; init; }
}

/// <summary>What a <see cref="RunicTestDialogRequest"/> asks for.</summary>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public enum RunicTestDialogKind
{
    /// <summary>A file dialog that selects one existing file (<c>open</c>).</summary>
    OpenFile,

    /// <summary>A file dialog that selects one or more existing files (<c>open-multiple</c>).</summary>
    OpenFiles,

    /// <summary>A file dialog that selects a save destination (<c>save</c>).</summary>
    SaveFile,

    /// <summary>A dialog that selects one directory (<c>folder</c>).</summary>
    OpenFolder,

    /// <summary>A dialog that selects one or more directories (<c>folder-multiple</c>).</summary>
    OpenFolders,

    /// <summary>A message box (<c>message-box</c>).</summary>
    MessageBox,

    /// <summary>A context menu (<c>show-context-menu</c>).</summary>
    ContextMenu,
}

/// <summary>
/// A dialog or context menu a guest asked a <see cref="RunicTestHost"/> to show for one of its windows (<c>dialogs/1</c>,
/// <c>menus/1</c>).
/// </summary>
/// <param name="WindowId">The window's id.</param>
/// <param name="Kind">The request's kind on the wire: <c>file-dialog</c>, <c>message-box</c> or <c>show-context-menu</c>.</param>
/// <param name="Message">The whole request as the guest sent it, with its members such as <c>dialog</c>, <c>buttons</c> or <c>menu</c>.</param>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record RunicTestDialogRequest(string WindowId, string Kind, JsonElement Message)
{
    /// <summary>What the guest asks for.</summary>
    public RunicTestDialogKind DialogKind => Kind switch
    {
        "message-box" => RunicTestDialogKind.MessageBox,
        "show-context-menu" => RunicTestDialogKind.ContextMenu,
        _ => Member("dialog") switch
        {
            "open-multiple" => RunicTestDialogKind.OpenFiles,
            "save" => RunicTestDialogKind.SaveFile,
            "folder" => RunicTestDialogKind.OpenFolder,
            "folder-multiple" => RunicTestDialogKind.OpenFolders,
            _ => RunicTestDialogKind.OpenFile,
        },
    };

    /// <summary>A dialog's or message box's title, or <see langword="null"/> for the platform's own.</summary>
    public string? Title => Member("title");

    /// <summary>The directory a file dialog opens in, or <see langword="null"/>.</summary>
    public string? InitialDirectory => Member("initialDirectory");

    /// <summary>The file name a save dialog suggests, or <see langword="null"/> for another kind.</summary>
    public string? SuggestedName => Member("suggestedName");

    /// <summary>The names of a file dialog's filters, in order: the index of one is what <see cref="RunicTestDialogAnswer.Select"/> chooses.</summary>
    public IReadOnlyList<string> FilterNames => Names("filters", "name");

    /// <summary>A message box's message, or <see langword="null"/> for another kind.</summary>
    public string? Text => Kind == "message-box" ? Member("message") : null;

    /// <summary>A message box's detail, or <see langword="null"/>.</summary>
    public string? Detail => Member("detail");

    /// <summary>The ids of a message box's buttons, in the guest's order; empty for one OK button or another kind.</summary>
    public IReadOnlyList<string> ButtonIds => Names("buttons", "id");

    private string? Member(string name) =>
        Message.ValueKind == JsonValueKind.Object && Message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private string[] Names(string list, string name) =>
        Message.ValueKind == JsonValueKind.Object && Message.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array
            ? [.. items.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "")]
            : [];
}

/// <summary>
/// How the user of a <see cref="RunicTestHost"/> answers a dialog or context menu (<see cref="RunicTestHostOptions.FileDialog"/>,
/// <see cref="RunicTestHostOptions.MessageBox"/>, <see cref="RunicTestHostOptions.ContextMenu"/>), or how the host's
/// platform or policy turns it away.
/// </summary>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed class RunicTestDialogAnswer
{
    private RunicTestDialogAnswer(JsonObject? reply, (string Kind, string? Reason, string Message)? failure, IReadOnlyList<string>? paths = null, int? filter = null)
    {
        Reply = reply;
        Failure = failure;
        Paths = paths;
        Filter = filter;
    }

    internal JsonObject? Reply { get; }
    internal (string Kind, string? Reason, string Message)? Failure { get; }
    internal IReadOnlyList<string>? Paths { get; }
    internal int? Filter { get; }

    /// <summary>The user selected files or directories in a file dialog, and the filter of the index in <see cref="RunicTestDialogRequest.FilterNames"/>.</summary>
    /// <param name="paths">The absolute paths, at least one.</param>
    /// <param name="filter">The index of the filter the user chose, or <see langword="null"/>.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="ArgumentException">A path is not absolute, or there is none.</exception>
    public static RunicTestDialogAnswer Select(IReadOnlyList<string> paths, int? filter = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0) throw new ArgumentException("A file dialog answer selects at least one path; use Dismiss for none.", nameof(paths));
        foreach (var path in paths)
        {
            if (path is null || !Path.IsPathFullyQualified(path))
                throw new ArgumentException($"A file dialog answer selects absolute paths, not '{path}': the guest opens what the host's dialog returned.", nameof(paths));
        }
        if (filter < 0) throw new ArgumentOutOfRangeException(nameof(filter), filter, "A filter index is not negative.");
        return new(null, null, [.. paths], filter);
    }

    /// <summary>The user chose a message box's button or a context menu's item, by its id; <see langword="null"/> for none.</summary>
    /// <param name="id">The id, such as from <see cref="RunicTestHostedWindow.FindMenuItem(JsonElement, string[])"/>.</param>
    /// <returns>The answer.</returns>
    public static RunicTestDialogAnswer Choose(string? id) => new(new JsonObject { ["outcome"] = "success", ["chosen"] = id }, null);

    /// <summary>The user closed the dialog or menu without a choice: a file dialog is dismissed, a box returns no button.</summary>
    /// <returns>The answer.</returns>
    public static RunicTestDialogAnswer Dismiss() => new(new JsonObject { ["outcome"] = "dismissed" }, null);

    /// <summary>The host's policy refuses it (<c>rejected</c>, <c>refused</c>): the guest receives <see cref="PlatformFailureCode.PermissionDenied"/>.</summary>
    /// <param name="message">Why, for the guest.</param>
    /// <returns>The answer.</returns>
    public static RunicTestDialogAnswer Refuse(string? message = null) => new(null, ("rejected", "refused", message ?? "The test host refused it."));

    /// <summary>The host's platform failed to show it, with a failure code (a <c>failed</c> outcome).</summary>
    /// <param name="code">The code.</param>
    /// <returns>The answer.</returns>
    public static RunicTestDialogAnswer Fail(PlatformFailureCode code) =>
        new(new JsonObject { ["outcome"] = "failed", ["code"] = HostProtocol.Kebab(code.ToString()) }, null);

    /// <summary>The host could not show it (an <c>unavailable</c> outcome), such as <see cref="PlatformUnavailableReason.OwnerClosed"/>.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns>The answer.</returns>
    public static RunicTestDialogAnswer Unavailable(PlatformUnavailableReason reason) =>
        new(new JsonObject { ["outcome"] = "unavailable", ["reason"] = HostProtocol.Kebab(reason.ToString()) }, null);
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
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cancellation source has no timer; it holds nothing to release.")]
public sealed partial class RunicTestHostedWindow
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
    public Views.RunicWindowPresentationFeatures Features { get; internal set; }

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

    internal void MarkClosed()
    {
        _closed.TrySetResult();
        EndUserRequests();
    }

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
        _active = activity.IsActive;
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
    /// Reports the window's placement to the guest, as the <c>placement</c> push does after the user moved, resized or
    /// maximized it: the bounds it restores to, which the guest's <see cref="Views.IRunicWindowPresentation.Placement"/>
    /// reports, also as its current frame.
    /// </summary>
    /// <param name="placement">The bounds the window restores to and whether it is maximized or full-screen.</param>
    /// <param name="cancellationToken">Cancels sending.</param>
    /// <returns>A task that completes when the push was sent.</returns>
    public Task SetPlacementAsync(Views.RunicWindowPlacement placement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return _host.PushAsync(new JsonObject
        {
            ["kind"] = "placement",
            ["window"] = Id,
            ["bounds"] = new JsonObject { ["x"] = placement.X ?? 0, ["y"] = placement.Y ?? 0, ["width"] = placement.Width, ["height"] = placement.Height },
            ["maximized"] = placement.Maximized,
            ["fullScreen"] = placement.FullScreen,
            ["monitor"] = null,
            ["restored"] = new JsonObject { ["x"] = placement.X, ["y"] = placement.Y, ["width"] = placement.Width, ["height"] = placement.Height },
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
