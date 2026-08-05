using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RunicToolkit.Hosting;

/// <summary>Provides immutable inputs to application validators.</summary>
public sealed class ApplicationValidationContext
{
    /// <summary>Initializes a validation context.</summary>
    public ApplicationValidationContext(LaunchDecision decision)
    {
        Decision = decision ?? throw new System.ArgumentNullException(nameof(decision));
    }

    /// <summary>Gets the selected launch decision.</summary>
    public LaunchDecision Decision { get; }
}

/// <summary>Contains one deterministic, safe validation error.</summary>
public sealed record ApplicationValidationError(string Code, string SafeMessage);

/// <summary>Validates common or selected-mode configuration.</summary>
public interface IApplicationValidator
{
    /// <summary>Adds all deterministic errors found by this validator.</summary>
    ValueTask ValidateAsync(
        ApplicationValidationContext context,
        ICollection<ApplicationValidationError> errors,
        CancellationToken cancellationToken);
}
