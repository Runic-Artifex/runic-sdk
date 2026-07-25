using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.Hosting.WebUi;

/// <summary>Validates manifest-backed UI assets before host or native-runtime startup.</summary>
public sealed class FrontendAssetValidator : IApplicationValidator
{
    private readonly IFrontendAssetProvider _provider;

    /// <summary>Initializes the selected-mode validator.</summary>
    public FrontendAssetValidator(IFrontendAssetProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <inheritdoc />
    public async ValueTask ValidateAsync(
        ApplicationValidationContext context,
        ICollection<ApplicationValidationError> errors,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(errors);
        try
        {
            await _provider.ValidateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            errors.Add(new ApplicationValidationError(
                ApplicationFailureCodes.Validation,
                "The frontend asset manifest or its content is invalid."));
        }
    }
}
