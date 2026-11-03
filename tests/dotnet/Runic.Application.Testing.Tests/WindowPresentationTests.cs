using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// B1: IRunicWindowPresentation in a Window's scope forwards to the host's presentation and raises its changes in the
// Window's model turns; the recording fake of Runic.Application.Testing drives a ViewModel test.
internal sealed class WindowPresentationTests
{
    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddRunicReactiveModelContext();
        services.AddScoped<WindowPresentationViewModel>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, WindowPresentationViewModel, IDisposable>>(
            _ => (transport, content, vm) => new WindowPresentationBridge(transport, vm, content: content));
        services.AddRunicWindowService();
        return services;
    }

    [Test]
    public async Task TheScopeAndTheWindowShareThePresentationOfTheHostAsync()
    {
        await using var provider = Services().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var backend = new RunicTestWindowPresentation();
        await using var window = await new PresentingHost(provider, backend).OpenWindowAsync<WindowPresentationWindow>();
        var presentation = window.ViewModel.Window;
        Require(ReferenceEquals(presentation, window.Presentation), "The Window and its ViewModel received different presentations.");
        Require(ReferenceEquals(presentation, window.Services.GetRequiredService<IRunicWindowPresentation>()),
            "The scope resolved another presentation.");
        Require(presentation.Features == RunicTestWindowPresentation.AllFeatures, $"Features: {presentation.Features}");

        var hidden = await presentation.SetVisibleAsync(false);
        Require(hidden is PlatformResult<PlatformUnit>.Success, $"Hiding reported {hidden}.");
        Require(backend.Requests is [var only] && only == new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.SetVisible) { Value = false },
            "The request did not reach the host.");
        await WaitUntilAsync(() => window.ViewModel.Changes is [.., var last] && !last.Activity.IsVisible);
        var change = window.ViewModel.Changes[^1];
        Require(change.Activity == RunicWindowActivity.Closed && change.InTurn,
            $"The change {change} was not raised in a model turn.");

        var activation = new DesktopActivationContext(activationToken: "token");
        Require(await presentation.PresentAsync(activation) is PlatformResult<PlatformUnit>.Success, "Presenting failed.");
        Require(backend.Requests[^1].Activation == activation, "The activation context did not reach the host.");
        await WaitUntilAsync(() => presentation.IsActive && presentation.IsVisible);

        Require(await presentation.SetColorSchemeAsync(DesktopColorScheme.Dark) is PlatformResult<PlatformUnit>.Success, "The scheme failed.");
        Require(presentation.ColorScheme == DesktopColorScheme.Dark, "The Window does not report the host's scheme.");

        await window.DisposeAsync();
        Require(await presentation.MinimizeAsync() is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed },
            "A released Window still forwards requests.");
        Require(presentation.Features == RunicWindowPresentationFeatures.None, "A released Window still reports features.");
        await WaitUntilAsync(() => presentation.Activity == RunicWindowActivity.Closed);
    }

    [Test]
    public async Task PropertyChangesFollowTheActivityAsync()
    {
        await using var provider = Services().BuildServiceProvider();
        var backend = new RunicTestWindowPresentation();
        await using var window = await new PresentingHost(provider, backend).OpenWindowAsync<WindowPresentationWindow>();
        var changed = new List<string?>();
        window.Presentation.PropertyChanged += (_, args) => { lock (changed) changed.Add(args.PropertyName); };
        backend.SetActive(false);
        await WaitUntilAsync(() => { lock (changed) return changed.Count >= 2; });
        lock (changed) Require(changed.SequenceEqual([nameof(IRunicWindowPresentation.IsActive), nameof(IRunicWindowPresentation.Activity)]),
            $"Raised: {string.Join(", ", changed)}");

        var observed = new List<RunicWindowActivity>();
        using (window.Presentation.ObserveActivity().Subscribe(new Observer(observed)))
        {
            Require(observed is [var first] && first == window.Presentation.Activity, "ObserveActivity did not start with the current activity.");
            backend.SetActivity(new RunicWindowActivity(false, true, false));
            await WaitUntilAsync(() => { lock (observed) return observed.Count == 2; });
        }
        backend.SetActive(true);
        await WaitUntilAsync(() => window.Presentation.IsActive);
        Require(observed.Count == 2, "A disposed subscription still observes.");
    }

    [Test]
    public async Task WithoutAHostPresentationRequestsReportTheOwnerUnavailableAsync()
    {
        await using var provider = Services().BuildServiceProvider();
        // TestWindowHost attaches no presentation, as a serve-only host does.
        await using var window = await new TestWindowHost(provider).OpenWindowAsync<WindowPresentationWindow>();
        var presentation = window.ViewModel.Window;
        Require(presentation.Activity == RunicWindowActivity.Assumed, "A Window without a presentation is not assumed active.");
        Require(await presentation.PresentAsync() is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerUnavailable },
            "Presenting without a host presentation did not report OwnerUnavailable.");

        // A scope that no Window opened.
        await using var scope = provider.CreateAsyncScope();
        var unbound = scope.ServiceProvider.GetRequiredService<IRunicWindowPresentation>();
        Require(ReferenceEquals(unbound, scope.ServiceProvider.GetRequiredService<IRunicWindowPresentation>()), "The scope has two presentations.");
        Require(await unbound.SetVisibleAsync(false) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerUnavailable },
            "An unbound presentation did not report OwnerUnavailable.");
        await Catch<ArgumentOutOfRangeException>(() => unbound.SetColorSchemeAsync((DesktopColorScheme)7).AsTask());
    }

    [Test]
    public async Task TheFakeRecordsRequestsAndDrivesTheViewModelInATurnAsync()
    {
        var services = Services();
        services.AddRunicTestWindowPresentation();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        using var host = RunicWindowTestHost.Create<WindowPresentationViewModel>(scope.ServiceProvider);
        var fake = host.Presentation ?? throw new InvalidOperationException("The host has no recording presentation.");
        Require(ReferenceEquals(fake, host.ViewModel.Window), "The ViewModel received another presentation.");

        await host.SetWindowActivityAsync(new RunicWindowActivity(IsActive: false, IsMinimized: false, IsVisible: true));
        var change = host.ViewModel.Changes is [var single] ? single : throw new InvalidOperationException("Expected one change.");
        Require(!change.Activity.IsActive && change.InTurn, $"The fake's change {change} did not run in a turn.");

        Require(await fake.MinimizeAsync() is PlatformResult<PlatformUnit>.Success && fake.IsMinimized && !fake.IsVisible,
            "Minimizing did not minimize the fake.");
        Require(await fake.PresentAsync() is PlatformResult<PlatformUnit>.Success && fake.IsActive && fake.PresentCount == 1,
            "Presenting did not activate the fake.");
        Require(await fake.SetDocumentEditedAsync(true) is PlatformResult<PlatformUnit>.Success && fake.IsDocumentEdited, "Not edited.");
        Require(await fake.SetFullScreenAsync(true) is PlatformResult<PlatformUnit>.Success && fake.IsFullScreen, "Not full-screen.");

        fake.Features = RunicWindowPresentationFeatures.Present;
        Require(await fake.SetVisibleAsync(false) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }
            && fake.IsVisible, "A request without its feature changed the fake.");
        fake.Respond = request => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied);
        Require(await fake.PresentAsync() is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied },
            "Respond did not choose the result.");
        Require(fake.Requests.Count == 6 && fake.PresentCount == 2, $"Recorded {fake.Requests.Count} requests.");
        fake.ClearRequests();
        Require(fake.Requests.Count == 0, "The requests were not cleared.");
    }

    // Opens Windows on RunicWindowTestHost, as TestWindowHost does, and gives each the presentation of its native window.
    private sealed class PresentingHost(IServiceProvider services, IRunicWindowPresentation presentation) : IRunicWindowHost
    {
        private readonly TestWindowHost _inner = new(services);

        public IServiceProvider Services => _inner.Services;

        public IReadOnlyList<RunicWindowDiagnostic> Validate(RunicWindowOptions options) => [];

        public async ValueTask<IBridgeWindow> OpenAsync<TViewModel>(RunicWindowOpening<TViewModel> opening, CancellationToken cancellationToken)
            where TViewModel : class
        {
            var window = await _inner.OpenAsync(opening, cancellationToken);
            opening.UsePresentation(presentation);
            return window;
        }
    }

    private sealed class Observer(List<RunicWindowActivity> observed) : IObserver<RunicWindowActivity>
    {
        public void OnNext(RunicWindowActivity value) { lock (observed) observed.Add(value); }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 10)
        {
            if (waited > 5000) throw new TimeoutException("The condition did not become true.");
            await Task.Delay(10);
        }
    }

    private static async Task<T?> Catch<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
