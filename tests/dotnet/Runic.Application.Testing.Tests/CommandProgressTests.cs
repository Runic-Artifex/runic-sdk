using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// #1180 (API-10, D067.12): an asynchronous command's IProgress<double> parameter reports the operation's progress, as
// RunicOperationProgress.Report does, and is no input of the command on the wire.
internal sealed class CommandProgressTests
{
    [Test]
    public async Task AToolkitCommandReportsThroughItsProgressParameterAsync()
    {
        var probe = new CommandProgressProbe();
        var model = new ToolkitProgressViewModel(probe);
        using var host = new RunicWindowTestHost<ToolkitProgressViewModel>(model,
            (transport, content, vm) => new ToolkitProgressBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "toolkitProgress", ViewLocator = new TestViewLocator() });
        await ReportsProgressAsync(host.Root, vm => vm.ImportCommand, probe);
        await AwaitedCallHasNoOperationAsync(host.Root, vm => vm.ImportCommand, probe);
    }

    [Test]
    public async Task AReactiveCommandReportsThroughItsProgressParameterAsync()
    {
        await using var context = new RunicModelContext();
        var probe = new CommandProgressProbe();
        var model = new ReactiveProgressViewModel(context, probe);
        using var contextLease = RunicModelContextRegistry.Shared.Bind(context, model);
        using var host = new RunicWindowTestHost<ReactiveProgressViewModel>(model,
            (transport, content, vm) => new ReactiveProgressBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "reactiveProgress", ViewLocator = new TestViewLocator() });
        await ReportsProgressAsync(host.Root, vm => vm.ImportCommand, probe);
        await AwaitedCallHasNoOperationAsync(host.Root, vm => vm.ImportCommand, probe);
    }

    [Test]
    public void CurrentOutsideAnOperationDoesNothing()
    {
        var progress = RunicOperationProgress.Current;
        progress.Report(0.5);
        progress.Report(double.NaN);
        Require(ReferenceEquals(progress, RunicOperationProgress.Current), "Outside an operation, Current is not the one reporter that does nothing.");
    }

    // The command starts as an operation without input; a report on its flow and one from a worker thread reach a
    // status, and a report after it ended is ignored.
    private static async Task ReportsProgressAsync<TModel>(RunicViewDriver<TModel> driver,
        System.Linq.Expressions.Expression<Func<TModel, object?>> command, CommandProgressProbe probe) where TModel : class
    {
        probe.Reset();
        var operation = await driver.StartAsync(command, null, "import-1");
        Require(operation.Admission == RunicOperationAdmissionKind.Accepted, $"The command was not admitted: {operation.AdmissionJson}");
        var reported = false;
        for (var attempt = 0; attempt < 200 && !reported; attempt++)
        {
            using var status = JsonDocument.Parse((await operation.GetStatusAsync()).Json);
            reported = Find(status.RootElement, "progress") is { ValueKind: JsonValueKind.Number } progress && progress.GetDouble() == 0.5;
            if (!reported) await Task.Delay(10);
        }
        Require(reported, $"The worker thread's report did not reach the operation: {(await operation.GetStatusAsync()).Json}");
        Require(probe.Received is { } received && !ReferenceEquals(received, RunicOperationProgress.Current),
            "The command received the reporter that does nothing.");
        probe.ReleaseAll();
        var terminal = await operation.WaitAsync();
        Require(terminal.Kind == RunicOperationStatusKind.Succeeded, $"The operation did not succeed: {terminal.Json}");
        probe.Received!.Report(1);
        Require((await operation.GetStatusAsync()).Kind == RunicOperationStatusKind.Succeeded, "A late report changed the ended operation.");
    }

    // The ordinary awaited call runs outside an operation: the command still receives a reporter, which does nothing.
    private static async Task AwaitedCallHasNoOperationAsync<TModel>(RunicViewDriver<TModel> driver,
        System.Linq.Expressions.Expression<Func<TModel, object?>> command, CommandProgressProbe probe) where TModel : class
    {
        probe.Reset();
        probe.ReleaseAll();
        (await driver.ExecuteAsync(command)).ThrowIfFailed();
        Require(ReferenceEquals(probe.Received, RunicOperationProgress.Current),
            "An awaited call outside an operation did not receive the reporter that does nothing.");
    }

    private static JsonElement? Find(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name)) return property.Value;
                if (Find(property.Value, name) is { } found) return found;
            }
        return null;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
