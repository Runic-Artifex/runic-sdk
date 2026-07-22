using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.Hosting;

/// <summary>
/// Validates mode routing, common configuration, and selected-mode configuration in a
/// deterministic order before lifecycle startup begins.
/// </summary>
public sealed class ApplicationCompositionValidator : IApplicationValidator
{
    private readonly IApplicationModeRouteTable _routeTable;
    private readonly ReadOnlyCollection<IApplicationValidator> _commonValidators;
    private readonly ReadOnlyDictionary<LaunchKind, IReadOnlyList<IApplicationValidator>>
        _modeValidators;

    /// <summary>Initializes an immutable composition-validation pipeline.</summary>
    /// <param name="routeTable">The immutable mode-runner route table.</param>
    /// <param name="commonValidators">Validators that run for every valid launch.</param>
    /// <param name="modeValidators">Validators grouped by selected launch kind.</param>
    public ApplicationCompositionValidator(
        IApplicationModeRouteTable routeTable,
        IEnumerable<IApplicationValidator> commonValidators,
        IReadOnlyDictionary<LaunchKind, IReadOnlyList<IApplicationValidator>> modeValidators)
    {
        _routeTable = routeTable ?? throw new ArgumentNullException(nameof(routeTable));
        _commonValidators = Snapshot(commonValidators, nameof(commonValidators));
        _modeValidators = Snapshot(modeValidators, nameof(modeValidators));
    }

    /// <summary>Gets common validators in registration order.</summary>
    public IReadOnlyList<IApplicationValidator> CommonValidators => _commonValidators;

    /// <summary>Gets immutable selected-mode validator snapshots.</summary>
    public IReadOnlyDictionary<LaunchKind, IReadOnlyList<IApplicationValidator>> ModeValidators =>
        _modeValidators;

    /// <inheritdoc />
    public async ValueTask ValidateAsync(
        ApplicationValidationContext context,
        ICollection<ApplicationValidationError> errors,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(errors);

        cancellationToken.ThrowIfCancellationRequested();
        bool kindIsDefined = ValidateDecisionShape(context.Decision, errors);
        if (kindIsDefined && context.Decision.Kind != LaunchKind.Invalid)
        {
            ApplicationModeRouteSelection selection =
                _routeTable.SelectRunner(context.Decision.Kind);
            if (!selection.IsSuccess)
            {
                ApplicationModeRouteError error = selection.Error
                    ?? throw new InvalidOperationException(
                        "A failed mode-route selection did not contain error details.");
                errors.Add(new ApplicationValidationError(error.Code, error.SafeMessage));
            }
        }

        await RunValidatorsAsync(
            _commonValidators,
            context,
            errors,
            cancellationToken).ConfigureAwait(false);

        if (_modeValidators.TryGetValue(context.Decision.Kind, out var selectedValidators))
        {
            await RunValidatorsAsync(
                selectedValidators,
                context,
                errors,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool ValidateDecisionShape(
        LaunchDecision decision,
        ICollection<ApplicationValidationError> errors)
    {
        bool kindIsDefined = Enum.IsDefined(decision.Kind);
        if (!kindIsDefined)
        {
            errors.Add(ShapeError("The launch decision contains an unsupported launch kind."));
        }

        if (decision.Kind == LaunchKind.Command)
        {
            if (string.IsNullOrWhiteSpace(decision.CommandName)
                || ContainsControlCharacter(decision.CommandName))
            {
                errors.Add(ShapeError(
                    "Command launches must provide a safe non-empty command name."));
            }
        }
        else if (decision.CommandName is not null)
        {
            errors.Add(ShapeError("Only command launches can provide a command name."));
        }

        if (decision.Kind == LaunchKind.Invalid
            && (string.IsNullOrWhiteSpace(decision.Diagnostic)
                || ContainsControlCharacter(decision.Diagnostic)))
        {
            errors.Add(ShapeError(
                "Invalid launches must provide a safe non-empty diagnostic."));
        }

        return kindIsDefined;
    }

    private static ApplicationValidationError ShapeError(string safeMessage) =>
        new(ApplicationFailureCodes.RunnerSelection, safeMessage);

    private static bool ContainsControlCharacter(string? value)
    {
        if (value is null)
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static async ValueTask RunValidatorsAsync(
        IReadOnlyList<IApplicationValidator> validators,
        ApplicationValidationContext context,
        ICollection<ApplicationValidationError> errors,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < validators.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await validators[index]
                .ValidateAsync(context, errors, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static ReadOnlyCollection<IApplicationValidator> Snapshot(
        IEnumerable<IApplicationValidator> validators,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(validators, parameterName);
        List<IApplicationValidator> snapshot = [];
        foreach (IApplicationValidator validator in validators)
        {
            snapshot.Add(validator ?? throw new ArgumentException(
                "Validator registrations cannot contain null entries.",
                parameterName));
        }

        return Array.AsReadOnly(snapshot.ToArray());
    }

    private static ReadOnlyDictionary<LaunchKind, IReadOnlyList<IApplicationValidator>> Snapshot(
        IReadOnlyDictionary<LaunchKind, IReadOnlyList<IApplicationValidator>> validators,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(validators, parameterName);
        Dictionary<LaunchKind, IReadOnlyList<IApplicationValidator>> snapshot = [];
        foreach ((LaunchKind kind, IReadOnlyList<IApplicationValidator> registrations) in validators)
        {
            snapshot.Add(kind, Snapshot(
                registrations ?? throw new ArgumentException(
                    "Mode-validator collections cannot be null.",
                    parameterName),
                parameterName));
        }

        return new ReadOnlyDictionary<LaunchKind, IReadOnlyList<IApplicationValidator>>(snapshot);
    }
}
