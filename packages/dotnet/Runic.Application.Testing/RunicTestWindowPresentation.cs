using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Runic.Application.Views;
using Runic.Platform;

namespace Runic.Application.Testing;

/// <summary>
/// A recording <see cref="IRunicWindowPresentation"/> for ViewModel tests: it records every request, behaves like a
/// window that obeys them, and lets the test set the activity, for example to check that a ViewModel pauses polling
/// while its Window is inactive.
/// </summary>
/// <remarks>
/// <para>
/// Register it for the window's scope with <see cref="RunicTestWindowPresentationServiceCollectionExtensions.AddRunicTestWindowPresentation"/>,
/// or pass one to a ViewModel built by hand. <see cref="RunicWindowTestHost{TViewModel}.Presentation"/> returns the one
/// the host's services resolve, and <see cref="RunicWindowTestHost{TViewModel}.SetWindowActivityAsync"/> changes its
/// activity in a turn of the window's model context, as a real host does:
/// <code>
/// await using var services = new ServiceCollection().AddRunicViews().AddScoped&lt;InboxViewModel&gt;()
///     .AddRunicTestWindowPresentation().BuildServiceProvider();
/// await using var scope = services.CreateAsyncScope();
/// using var host = RunicWindowTestHost.Create&lt;InboxViewModel&gt;(scope.ServiceProvider);
/// (await host.Root.ExecuteAsync(vm =&gt; vm.HideToTrayCommand)).ThrowIfFailed();
/// Assert.False(host.Presentation!.IsVisible);
/// await host.SetWindowActivityAsync(RunicWindowActivity.Assumed);
/// </code>
/// </para>
/// <para>
/// A request whose feature is not in <see cref="Features"/> reports <see cref="PlatformUnavailableReason.BackendUnavailable"/>
/// without changing anything; <see cref="Respond"/> chooses another result. A successful
/// <see cref="PresentAsync"/> makes the window active and visible, <see cref="MinimizeAsync"/> minimizes it, and
/// <see cref="SetVisibleAsync"/> with <see langword="false"/> hides it. Changes are raised on the calling thread.
/// </para>
/// </remarks>
public sealed class RunicTestWindowPresentation : IRunicWindowPresentation
{
    /// <summary>Every feature a desktop window can have.</summary>
    public const RunicWindowPresentationFeatures AllFeatures = RunicWindowPresentationFeatures.Present |
        RunicWindowPresentationFeatures.Minimize | RunicWindowPresentationFeatures.Visibility |
        RunicWindowPresentationFeatures.FullScreen | RunicWindowPresentationFeatures.DocumentEdited |
        RunicWindowPresentationFeatures.ColorScheme | RunicWindowPresentationFeatures.Activity;

    private static readonly PlatformResult<PlatformUnit> Done = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    private static readonly PlatformResult<PlatformUnit> Unsupported = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);

    private readonly object _gate = new();
    private readonly List<RunicWindowPresentationRequest> _requests = [];
    private RunicWindowActivity _activity;
    private DesktopColorScheme _colorScheme;

    /// <summary>Creates a window that is active and visible, with every feature.</summary>
    public RunicTestWindowPresentation() : this(AllFeatures, RunicWindowActivity.Assumed)
    {
    }

    /// <summary>Creates a window with <paramref name="features"/> and <paramref name="activity"/>.</summary>
    /// <param name="features">What the window can do, such as <see cref="RunicWindowPresentationFeatures.Present"/> alone for a CS-WebUI window.</param>
    /// <param name="activity">The activity it starts with.</param>
    public RunicTestWindowPresentation(RunicWindowPresentationFeatures features, RunicWindowActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        Features = features;
        _activity = activity;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc />
    public event EventHandler<RunicWindowActivityChangedEventArgs>? ActivityChanged;

    /// <summary>Gets or sets what the window can do; the default is <see cref="AllFeatures"/>.</summary>
    public RunicWindowPresentationFeatures Features { get; set; }

    /// <summary>
    /// Gets or sets the result of each request whose feature is in <see cref="Features"/>, or <see langword="null"/> for
    /// success. A request that does not succeed changes nothing.
    /// </summary>
    /// <remarks>For example, a desktop that keeps another application in front:
    /// <c>request =&gt; request.Kind == RunicWindowPresentationRequestKind.Present ? new PlatformResult&lt;PlatformUnit&gt;.Failed(PlatformFailureCode.PermissionDenied) : null</c>.</remarks>
    public Func<RunicWindowPresentationRequest, PlatformResult<PlatformUnit>?>? Respond { get; set; }

    /// <summary>Gets every request in the order it arrived, including those that did not succeed.</summary>
    public IReadOnlyList<RunicWindowPresentationRequest> Requests
    {
        get { lock (_gate) return [.. _requests]; }
    }

    /// <summary>Gets how many times the window was asked to present itself.</summary>
    public int PresentCount
    {
        get { lock (_gate) return _requests.Count(static request => request.Kind == RunicWindowPresentationRequestKind.Present); }
    }

    /// <summary>Gets whether the window is full-screen after the requests so far.</summary>
    public bool IsFullScreen { get; private set; }

    /// <summary>Gets whether the window's document is marked edited after the requests so far.</summary>
    public bool IsDocumentEdited { get; private set; }

    /// <inheritdoc />
    public RunicWindowActivity Activity => Volatile.Read(ref _activity);

    /// <inheritdoc />
    public bool IsActive => Activity.IsActive;

    /// <inheritdoc />
    public bool IsMinimized => Activity.IsMinimized;

    /// <inheritdoc />
    public bool IsVisible => Activity.IsVisible;

    /// <inheritdoc />
    public DesktopColorScheme ColorScheme => _colorScheme;

    /// <summary>
    /// Changes the activity as the desktop would, and raises <see cref="ActivityChanged"/> and
    /// <see cref="PropertyChanged"/> on the calling thread when it changed.
    /// </summary>
    /// <param name="activity">The new activity.</param>
    public void SetActivity(RunicWindowActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        RunicWindowActivity previous;
        lock (_gate)
        {
            previous = _activity;
            if (Equals(previous, activity)) return;
            Volatile.Write(ref _activity, activity);
        }
        if (previous.IsActive != activity.IsActive) Raise(nameof(IsActive));
        if (previous.IsMinimized != activity.IsMinimized) Raise(nameof(IsMinimized));
        if (previous.IsVisible != activity.IsVisible) Raise(nameof(IsVisible));
        Raise(nameof(Activity));
        ActivityChanged?.Invoke(this, new RunicWindowActivityChangedEventArgs(previous, activity));
    }

    /// <summary>Makes the window active and visible, or inactive and still visible, as switching to another window does.</summary>
    /// <param name="active">Whether the window has the keyboard focus.</param>
    public void SetActive(bool active) => SetActivity(active
        ? new RunicWindowActivity(IsActive: true, IsMinimized: false, IsVisible: true)
        : Activity with { IsActive = false });

    /// <summary>Clears <see cref="Requests"/>.</summary>
    public void ClearRequests()
    {
        lock (_gate) _requests.Clear();
    }

    /// <inheritdoc />
    public ValueTask<PlatformResult<PlatformUnit>> PresentAsync(DesktopActivationContext? activation = null, CancellationToken cancellationToken = default) =>
        Request(new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.Present) { Activation = activation },
            RunicWindowPresentationFeatures.Present, () => SetActivity(new RunicWindowActivity(true, false, true)), cancellationToken);

    /// <inheritdoc />
    public ValueTask<PlatformResult<PlatformUnit>> MinimizeAsync(CancellationToken cancellationToken = default) =>
        Request(new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.Minimize), RunicWindowPresentationFeatures.Minimize,
            () => SetActivity(new RunicWindowActivity(false, true, false)), cancellationToken);

    /// <inheritdoc />
    public ValueTask<PlatformResult<PlatformUnit>> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        Request(new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.SetVisible) { Value = visible },
            RunicWindowPresentationFeatures.Visibility,
            () => SetActivity(visible ? Activity with { IsMinimized = false, IsVisible = true } : new RunicWindowActivity(false, false, false)),
            cancellationToken);

    /// <inheritdoc />
    public ValueTask<PlatformResult<PlatformUnit>> SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default) =>
        Request(new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.SetFullScreen) { Value = fullScreen },
            RunicWindowPresentationFeatures.FullScreen, () => IsFullScreen = fullScreen, cancellationToken);

    /// <inheritdoc />
    public ValueTask<PlatformResult<PlatformUnit>> SetDocumentEditedAsync(bool edited, CancellationToken cancellationToken = default) =>
        Request(new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.SetDocumentEdited) { Value = edited },
            RunicWindowPresentationFeatures.DocumentEdited, () => IsDocumentEdited = edited, cancellationToken);

    /// <inheritdoc />
    public ValueTask<PlatformResult<PlatformUnit>> SetColorSchemeAsync(DesktopColorScheme colorScheme, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(colorScheme)) throw new ArgumentOutOfRangeException(nameof(colorScheme));
        return Request(new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.SetColorScheme) { ColorScheme = colorScheme },
            RunicWindowPresentationFeatures.ColorScheme, () =>
            {
                if (_colorScheme == colorScheme) return;
                _colorScheme = colorScheme;
                Raise(nameof(ColorScheme));
            }, cancellationToken);
    }

    private ValueTask<PlatformResult<PlatformUnit>> Request(RunicWindowPresentationRequest request, RunicWindowPresentationFeatures feature,
        Action apply, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _requests.Add(request);
        var result = (Features & feature) == 0 ? Unsupported : Respond?.Invoke(request) ?? Done;
        if (result is PlatformResult<PlatformUnit>.Success) apply();
        return ValueTask.FromResult(result);
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>A request to a <see cref="RunicTestWindowPresentation"/>, as <see cref="RunicTestWindowPresentation.Requests"/> records it.</summary>
/// <param name="Kind">Which request it was.</param>
public sealed record RunicWindowPresentationRequest(RunicWindowPresentationRequestKind Kind)
{
    /// <summary>Gets the value of <see cref="RunicWindowPresentationRequestKind.SetVisible"/>, <see cref="RunicWindowPresentationRequestKind.SetFullScreen"/> and <see cref="RunicWindowPresentationRequestKind.SetDocumentEdited"/>.</summary>
    public bool? Value { get; init; }

    /// <summary>Gets the scheme of <see cref="RunicWindowPresentationRequestKind.SetColorScheme"/>.</summary>
    public DesktopColorScheme? ColorScheme { get; init; }

    /// <summary>Gets the activation context of <see cref="RunicWindowPresentationRequestKind.Present"/>.</summary>
    public DesktopActivationContext? Activation { get; init; }
}

/// <summary>Which <see cref="IRunicWindowPresentation"/> request a <see cref="RunicWindowPresentationRequest"/> records.</summary>
public enum RunicWindowPresentationRequestKind
{
    /// <summary><see cref="IRunicWindowPresentation.PresentAsync"/>.</summary>
    Present,

    /// <summary><see cref="IRunicWindowPresentation.MinimizeAsync"/>.</summary>
    Minimize,

    /// <summary><see cref="IRunicWindowPresentation.SetVisibleAsync"/>.</summary>
    SetVisible,

    /// <summary><see cref="IRunicWindowPresentation.SetFullScreenAsync"/>.</summary>
    SetFullScreen,

    /// <summary><see cref="IRunicWindowPresentation.SetDocumentEditedAsync"/>.</summary>
    SetDocumentEdited,

    /// <summary><see cref="IRunicWindowPresentation.SetColorSchemeAsync"/>.</summary>
    SetColorScheme,
}

/// <summary>Registers <see cref="RunicTestWindowPresentation"/> for dependency injection.</summary>
public static class RunicTestWindowPresentationServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="RunicTestWindowPresentation"/> per scope as the scope's <see cref="IRunicWindowPresentation"/>,
    /// replacing the registration of <c>AddRunicWindowService()</c>, so the application's own registration method can
    /// run unchanged in the test.
    /// </summary>
    /// <param name="services">The test's services.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddRunicTestWindowPresentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.RemoveAll<IRunicWindowPresentation>();
        services.TryAddScoped<RunicTestWindowPresentation>();
        services.AddScoped<IRunicWindowPresentation>(static provider => provider.GetRequiredService<RunicTestWindowPresentation>());
        return services;
    }
}
