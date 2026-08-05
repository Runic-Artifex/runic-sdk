using System.Threading;
using System.Threading.Tasks;
using RunicToolkit.Hosting;

var builder = new GenericHostRunicToolkitApplicationBuilder(args);
builder.Application.AddModeRunner(new ExitRunner(LaunchKind.UserInterface, 0));
builder.Application.AddModeRunner(new ExitRunner(LaunchKind.Command, 17));
builder.Application.AddModeRunner(new ExitRunner(LaunchKind.Help, 0));
builder.Application.AddModeRunner(new ExitRunner(LaunchKind.Version, 0));

await using RunicToolkitApplication application = builder.Build();
ApplicationRunResult result = await application.RunAsync(args);
return result.ExitCode ?? 1;

internal sealed class ExitRunner(LaunchKind kind, int exitCode) : IApplicationModeRunner
{
    public LaunchKind Kind { get; } = kind;

    public Task<ApplicationRunResult> RunAsync(
        LaunchDecision decision,
        CancellationToken cancellationToken) =>
        Task.FromResult(ApplicationRunResult.FromExitCode(exitCode));
}
