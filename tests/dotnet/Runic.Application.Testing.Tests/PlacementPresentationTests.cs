using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// sdk#897, W1b (window-api-0.19/DESIGN.md §3.2, §3.4): maximize, restore and placement in a Window's scope, the recording
// fake's simulation, and the default interface members of a presentation written for 0.18.
internal sealed class PlacementPresentationTests
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
    public async Task TheFakeRecordsAndSimulatesThePlacementAsync()
    {
        var fake = new RunicTestWindowPresentation();
        Require(fake.Features.HasFlag(RunicWindowPresentationFeatures.Maximize | RunicWindowPresentationFeatures.Placement), "The fake cannot place.");
        Require(fake.Placement == new RunicWindowPlacement { X = 100, Y = 100, Width = 800, Height = 600 }, $"The fake starts at {fake.Placement}.");

        var raised = new List<string?>();
        fake.PropertyChanged += (_, args) => raised.Add(args.PropertyName);
        await fake.SetFullScreenAsync(true);
        Require(fake.Placement is { FullScreen: true }, "Full screen is not in the placement.");
        Require(await fake.MaximizeAsync() is PlatformResult<PlatformUnit>.Success, "Maximizing failed.");
        Require(fake.Placement is { Maximized: true, FullScreen: false } && !fake.IsFullScreen, "Maximizing did not leave full screen.");

        // Restore from minimized while maximized ends normal, and the window comes back active (D064.13).
        await fake.MinimizeAsync();
        Require(await fake.RestoreAsync() is PlatformResult<PlatformUnit>.Success, "Restoring failed.");
        Require(fake.Placement is { Maximized: false } && fake.Activity == RunicWindowActivity.Assumed,
            $"Restoring left {fake.Placement}, {fake.Activity}.");

        var placement = new RunicWindowPlacement { X = 40, Y = 50, Width = 1024, Height = 0, Maximized = true };
        Require(await fake.SetPlacementAsync(placement) is PlatformResult<PlatformUnit>.Success, "Placing failed.");
        Require(fake.Placement == new RunicWindowPlacement { X = 40, Y = 50, Width = 1024, Height = 600, Maximized = true },
            $"A height of 0 did not keep the height: {fake.Placement}.");
        await Catch<ArgumentException>(() => fake.SetPlacementAsync(new RunicWindowPlacement { Y = 1 }).AsTask());
        Require(fake.Requests.Select(static request => request.Kind).SequenceEqual([
            RunicWindowPresentationRequestKind.SetFullScreen, RunicWindowPresentationRequestKind.Maximize,
            RunicWindowPresentationRequestKind.Minimize, RunicWindowPresentationRequestKind.Restore,
            RunicWindowPresentationRequestKind.SetPlacement]), $"Recorded: {string.Join(", ", fake.Requests.Select(static r => r.Kind))}");
        Require(ReferenceEquals(fake.Requests[^1].Placement, placement), "The placement was not recorded as passed.");
        Require(raised.Contains(nameof(IRunicWindowPresentation.Placement)), $"Raised: {string.Join(", ", raised)}");

        // The user moves the window: not a request.
        fake.SetPlacement(new RunicWindowPlacement { X = 0, Y = 0, Width = 640, Height = 480 });
        Require(fake.Placement == new RunicWindowPlacement { X = 0, Y = 0, Width = 640, Height = 480 } && fake.Requests.Count == 5,
            "SetPlacement did not move the window without a request.");

        // A refusal, as a host's policy decides, changes nothing; without the features the requests are unavailable.
        fake.Respond = static request => request.Kind == RunicWindowPresentationRequestKind.Maximize
            ? new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied)
            : null;
        Require(await fake.MaximizeAsync() is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied }
            && fake.Placement is { Maximized: false }, "A refused maximize changed the window.");
        fake.Features = RunicTestWindowPresentation.AllFeatures & ~(RunicWindowPresentationFeatures.Maximize | RunicWindowPresentationFeatures.Placement);
        Require(fake.Placement is null, "A fake without Placement reports one.");
        Require(await fake.RestoreAsync() is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }
            && await fake.SetPlacementAsync(placement) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
            "A fake without the features did not report them unavailable.");
    }

    [Test]
    public async Task TheScopesPlacementChangesArriveInOneTurnAsync()
    {
        var services = Services();
        services.AddRunicTestWindowPresentation();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        using var host = RunicWindowTestHost.Create<WindowPresentationViewModel>(scope.ServiceProvider);
        var fake = host.Presentation!;
        var window = host.ViewModel.Window;
        await host.FlushWindowPresentationAsync();
        Require(window.Placement == fake.Placement, $"The Window reports {window.Placement}, the fake {fake.Placement}.");

        var raised = new List<(string? Property, bool InTurn, RunicWindowPlacement? Placement)>();
        window.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(IRunicWindowPresentation.Placement))
                lock (raised) raised.Add((args.PropertyName, RunicModelContext.Current is not null, window.Placement));
        };
        // A drag: the moves wait for one pending turn at a time, so the ViewModel sees at most each of them, in a turn,
        // and ends at the last.
        RunicWindowPlacement last = null!;
        for (var x = 1; x <= 3; x++) fake.SetPlacement(last = new RunicWindowPlacement { X = x, Y = 0, Width = 640, Height = 480 });
        await host.FlushWindowPresentationAsync();
        lock (raised)
            Require(raised.Count is >= 1 and <= 3 && raised.All(static change => change.InTurn) && raised[^1].Placement == last && window.Placement == last,
                $"Raised: {string.Join(", ", raised)}");

        Require(await window.MaximizeAsync() is PlatformResult<PlatformUnit>.Success
            && fake.Requests[^1].Kind == RunicWindowPresentationRequestKind.Maximize, "Maximize did not reach the fake.");
        await host.FlushWindowPresentationAsync();
        Require(window.Placement is { Maximized: true }, $"The Window reports {window.Placement}.");
        await Catch<ArgumentException>(() => window.SetPlacementAsync(new RunicWindowPlacement { X = 1 }).AsTask());

        await scope.DisposeAsync();
        Require(await window.RestoreAsync() is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed },
            "A closed Window did not report the owner closed.");
        Require(window.Placement is { Maximized: true }, "The placement is not readable after the Window closed.");
    }

    [Test]
    public async Task APresentationOfAnEarlierReleaseReportsBackendUnavailableAsync()
    {
        IRunicWindowPresentation legacy = new LegacyPresentation();
        Require(legacy.Placement is null, "The default placement is not null.");
        Require(await legacy.MaximizeAsync() is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }
            && await legacy.RestoreAsync() is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }
            && await legacy.SetPlacementAsync(new RunicWindowPlacement()) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
            "The default members did not report BackendUnavailable.");
        await Catch<ArgumentNullException>(() => legacy.SetPlacementAsync(null!).AsTask());
        await Catch<OperationCanceledException>(() => legacy.MaximizeAsync(new CancellationToken(canceled: true)).AsTask());

        // In a Window's scope, with the legacy presentation as its host, and in a scope without a host (serve-only).
        var services = Services();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var window = scope.ServiceProvider.GetRequiredService<IRunicWindowPresentation>();
        Require(await window.MaximizeAsync() is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerUnavailable }
            && window.Placement is null, "A Window without a host did not report the owner unavailable.");
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

    // IRunicWindowPresentation as a 0.18 host implemented it.
    private sealed class LegacyPresentation : IRunicWindowPresentation
    {
        private static readonly PlatformResult<PlatformUnit> Done = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event EventHandler<RunicWindowActivityChangedEventArgs>? ActivityChanged { add { } remove { } }
        public RunicWindowPresentationFeatures Features => RunicWindowPresentationFeatures.Present;
        public RunicWindowActivity Activity => RunicWindowActivity.Assumed;
        public bool IsActive => true;
        public bool IsMinimized => false;
        public bool IsVisible => true;
        public DesktopColorScheme ColorScheme => DesktopColorScheme.NoPreference;
        public ValueTask<PlatformResult<PlatformUnit>> PresentAsync(DesktopActivationContext? activation = null, CancellationToken cancellationToken = default) => ValueTask.FromResult(Done);
        public ValueTask<PlatformResult<PlatformUnit>> MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Done);
        public ValueTask<PlatformResult<PlatformUnit>> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.FromResult(Done);
        public ValueTask<PlatformResult<PlatformUnit>> SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default) => ValueTask.FromResult(Done);
        public ValueTask<PlatformResult<PlatformUnit>> SetDocumentEditedAsync(bool edited, CancellationToken cancellationToken = default) => ValueTask.FromResult(Done);
        public ValueTask<PlatformResult<PlatformUnit>> SetColorSchemeAsync(DesktopColorScheme colorScheme, CancellationToken cancellationToken = default) => ValueTask.FromResult(Done);
    }
}
