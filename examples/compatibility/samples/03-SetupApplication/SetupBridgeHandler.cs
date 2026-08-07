using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RunicToolkit.ApplicationBridge;
using RunicToolkit.Examples.Setup.Contract;

namespace RunicToolkit.Examples.Setup;

internal sealed class SetupBridgeHandler(bool failInstallation = false) : ISetupBridgeHandler
{
    private static readonly Guid RecommendedDestinationId =
        Guid.Parse("7e510a78-3c9a-4bed-8c31-2d93e5bbb835");
    private readonly Lock _gate = new();
    private readonly bool _failInstallation = failInstallation;
    private string _viewId = "Welcome";
    private long _revision;
    private Destination? _destination;
    private string[] _selectedFeatures = ["core"];
    private Guid? _activeOperationId;

    public ValueTask<ApplicationInitialized> InitializeApplicationAsync(
        InitializeApplication command,
        BridgeCommandContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(new ApplicationInitialized
            {
                Tag = "ApplicationInitialized",
                Snapshot = CreateInitializedSnapshot(),
            });
        }
    }

    public ValueTask<DestinationSelected> SelectDestinationAsync(
        SelectDestination command,
        BridgeCommandContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureNoActiveOperation();
            _destination = new Destination(
                RecommendedDestinationId,
                "Recommended local destination",
                12_000_000_000);
            _revision = context.CurrentRevision + 1;
            return ValueTask.FromResult(new DestinationSelected
            {
                Tag = "DestinationSelected",
                Destination = new DestinationSelectedDestination
                {
                    SelectionId = _destination.SelectionId,
                    DisplayName = _destination.DisplayName,
                    AvailableBytes = _destination.AvailableBytes,
                },
                Revision = _revision,
            });
        }
    }

    public ValueTask<NavigationAccepted> NavigateAsync(
        Navigate command,
        BridgeCommandContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (command.ExpectedRevision != context.CurrentRevision)
                throw new InvalidOperationException("Navigation was based on stale state.");
            if (_activeOperationId is not null && command.Target != "Installing")
                throw new InvalidOperationException("Navigation is locked while installation is active.");
            if (command.Target is "Features" or "Installing" && _destination is null)
                throw new InvalidOperationException("A destination must be selected first.");
            if (command.Target == "Complete" && _viewId != "Complete")
                throw new InvalidOperationException("Completion is controlled by the installation operation.");

            _viewId = command.Target;
            _revision = context.CurrentRevision + 1;
            return ValueTask.FromResult(new NavigationAccepted
            {
                Tag = "NavigationAccepted",
                Snapshot = CreateNavigationSnapshot(),
            });
        }
    }

    public ValueTask<InstallationStarted> StartInstallationAsync(
        StartInstallation command,
        BridgeCommandContext context,
        CancellationToken cancellationToken)
    {
        Destination destination;
        lock (_gate)
        {
            EnsureNoActiveOperation();
            destination = _destination ?? throw new InvalidOperationException("A destination must be selected first.");
            if (destination.SelectionId != command.DestinationSelectionId)
                throw new InvalidOperationException("The destination handle is not owned by this session.");
            if (!command.SelectedFeatures.Contains("core", StringComparer.Ordinal))
                throw new InvalidOperationException("The core feature is required.");
            _selectedFeatures = command.SelectedFeatures.Distinct(StringComparer.Ordinal).ToArray();
            _viewId = "Installing";
            _revision = context.CurrentRevision + 1;
        }

        BridgeOperationId operation = context.Operations.Start(
            (operationId, token) => RunInstallationAsync(operationId, context, token),
            cancellationToken);
        lock (_gate)
        {
            _activeOperationId = operation.Value;
        }
        return ValueTask.FromResult(new InstallationStarted
        {
            Tag = "InstallationStarted",
            CommandId = context.CommandId.Value,
            OperationId = operation.Value,
            Revision = _revision,
        });
    }

    public ValueTask<OperationCancellationAccepted> CancelOperationAsync(
        CancelOperation command,
        BridgeCommandContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new OperationCancellationAccepted
        {
            Tag = "OperationCancellationAccepted",
            OperationId = command.OperationId,
            Accepted = false,
            Revision = context.CurrentRevision,
        });

    private async ValueTask RunInstallationAsync(
        BridgeOperationId operationId,
        BridgeCommandContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            for (int step = 1; step <= 5; step++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
                await context.Events.PublishOperationProgressAsync(new OperationProgress
                {
                    Tag = "OperationProgress",
                    OperationId = operationId.Value,
                    Completed = step,
                    Total = 5,
                    Message = step == 5 ? "Finalizing" : $"Installing component {step} of 5",
                }, operationId: operationId, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (_failInstallation)
            {
                long failedRevision = FinishOperation(operationId.Value, "Features");
                await context.Events.PublishOperationFailedAsync(new OperationFailedEvent
                {
                    Tag = "OperationFailed",
                    OperationId = operationId.Value,
                    Error = "The simulated package verification failed.",
                    Revision = failedRevision,
                }, advancesRevision: true, operationId: operationId, cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }

            long completedRevision = FinishOperation(operationId.Value, "Complete");
            await context.Events.PublishOperationCompletedAsync(new OperationCompleted
            {
                Tag = "OperationCompleted",
                OperationId = operationId.Value,
                Revision = completedRevision,
            }, advancesRevision: true, operationId: operationId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            long cancelledRevision = FinishOperation(operationId.Value, "Features");
            await context.Events.PublishOperationCancelledAsync(new OperationCancelledEvent
            {
                Tag = "OperationCancelled",
                OperationId = operationId.Value,
                Revision = cancelledRevision,
            }, advancesRevision: true, operationId: operationId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private long FinishOperation(Guid operationId, string nextView)
    {
        lock (_gate)
        {
            if (_activeOperationId != operationId)
                throw new InvalidOperationException("The operation no longer owns this session.");
            _activeOperationId = null;
            _viewId = nextView;
            return ++_revision;
        }
    }

    private ApplicationInitializedSnapshot CreateInitializedSnapshot() => new()
    {
        ViewId = _viewId,
        Revision = _revision,
        Destination = _destination is null ? null : new ApplicationInitializedSnapshotDestination
        {
            SelectionId = _destination.SelectionId,
            DisplayName = _destination.DisplayName,
            AvailableBytes = _destination.AvailableBytes,
        },
        SelectedFeatures = _selectedFeatures,
        ActiveOperationId = _activeOperationId,
        CanNavigateBack = CanNavigateBack(),
        CanNavigateNext = CanNavigateNext(),
    };

    private NavigationAcceptedSnapshot CreateNavigationSnapshot() => new()
    {
        ViewId = _viewId,
        Revision = _revision,
        Destination = _destination is null ? null : new NavigationAcceptedSnapshotDestination
        {
            SelectionId = _destination.SelectionId,
            DisplayName = _destination.DisplayName,
            AvailableBytes = _destination.AvailableBytes,
        },
        SelectedFeatures = _selectedFeatures,
        ActiveOperationId = _activeOperationId,
        CanNavigateBack = CanNavigateBack(),
        CanNavigateNext = CanNavigateNext(),
    };

    private bool CanNavigateBack() => _activeOperationId is null && _viewId is not "Welcome" and not "Complete";
    private bool CanNavigateNext() => _activeOperationId is null && _viewId switch
    {
        "Welcome" => true,
        "Destination" => _destination is not null,
        "Features" => true,
        _ => false,
    };
    private void EnsureNoActiveOperation()
    {
        if (_activeOperationId is not null)
            throw new InvalidOperationException("Installation is already active.");
    }

    private sealed record Destination(Guid SelectionId, string DisplayName, long AvailableBytes);
}
