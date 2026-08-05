using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RunicToolkit.Hosting;

var builder = new GenericHostRunicToolkitApplicationBuilder();
builder.Configuration["sample"] = "custom";
builder.Application.AddValidator(new RequiredConfigurationValidator(
    () => builder.Configuration["sample"]));
builder.Application.AddModeRunner(new SuccessfulUiRunner());
builder.Application.ConfigureTimeouts(options =>
{
    options.StartupTimeout = TimeSpan.FromSeconds(5);
    options.TotalShutdownTimeout = TimeSpan.FromSeconds(5);
});

await using RunicToolkitApplication application = builder.Build();
ApplicationRunResult result = await application.RunAsync();
return result.ExitCode ?? 1;

internal sealed class RequiredConfigurationValidator(Func<string?> read) : IApplicationValidator
{
    public ValueTask ValidateAsync(
        ApplicationValidationContext context,
        ICollection<ApplicationValidationError> errors,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(read()))
        {
            errors.Add(new ApplicationValidationError(
                ApplicationFailureCodes.Validation,
                "Required sample configuration is missing."));
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class SuccessfulUiRunner : IApplicationModeRunner
{
    public LaunchKind Kind => LaunchKind.UserInterface;
    public Task<ApplicationRunResult> RunAsync(
        LaunchDecision decision,
        CancellationToken cancellationToken) =>
        Task.FromResult(ApplicationRunResult.FromExitCode(0));
}
