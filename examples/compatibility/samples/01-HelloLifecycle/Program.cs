using System;
using System.Threading;
using System.Threading.Tasks;
using RunicToolkit.Hosting;

var builder = new GenericHostRunicToolkitApplicationBuilder(args);

// A participant prepares application services before the selected mode runs.
builder.Application.AddStartupParticipant(new WorkspaceParticipant());

// A mode runner contains the work for one launch kind.
builder.Application.AddModeRunner(new WelcomeMode());

await using RunicToolkitApplication application = builder.Build();
ApplicationRunResult result = await application.RunAsync(args);

Console.WriteLine($"Application finished in state {application.State}.");
return result.ExitCode ?? 1;

internal sealed class WorkspaceParticipant : IApplicationStartupParticipant
{
    public ApplicationStartPhase Phase => ApplicationStartPhase.Infrastructure;

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.WriteLine("Preparing the workspace...");
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.WriteLine("Closing the workspace...");
        return ValueTask.CompletedTask;
    }
}

internal sealed class WelcomeMode : IApplicationModeRunner
{
    public LaunchKind Kind => LaunchKind.UserInterface;

    public Task<ApplicationRunResult> RunAsync(
        LaunchDecision decision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.WriteLine();
        Console.WriteLine("Hello from Runic Toolkit!");
        Console.WriteLine($"The launcher selected the {decision.Kind} mode.");
        Console.WriteLine();
        return Task.FromResult(ApplicationRunResult.FromExitCode(0));
    }
}
