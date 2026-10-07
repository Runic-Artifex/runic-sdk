using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// W130-029 slice 3: declared failures through generated bridges for a sync
// RelayCommand, an AsyncRelayCommand, a plain ICommand and a ReactiveCommand,
// on the awaited route and as operations, plus golden generated output.
internal static class DomainFailureCodegenTests
{
    internal static async Task RunAsync()
    {
        await ToolkitCommandsAsync();
        await ReactiveCommandAsync();
        GoldenOutput();
    }

    private static async Task ToolkitCommandsAsync()
    {
        var model = new FailureToolkitViewModel();
        using var host = new RunicWindowTestHost<FailureToolkitViewModel>(model, "failureToolkit",
            (transport, content, vm) => new FailureToolkitBridge(transport, vm, content: content), new TestViewLocator());
        var driver = host.Root;

        // Sync RelayCommand, declared on its [RelayCommand] method: a [RunicUnion] failure.
        RequireFailure(await driver.ExecuteAsync(vm => vm.SaveCommand), """{"$case":"titleRequired"}""");
        model.Title = "taken";
        RequireFailure(await driver.ExecuteAsync(vm => vm.SaveCommand), """{"$case":"titleTaken","existingTitle":"Todo"}""");
        model.Title = "fresh";
        (await driver.ExecuteAsync(vm => vm.SaveCommand)).EnsureOk();
        Require(model.Title == "saved", "The undeclared path of a declared command did not run.");

        // AsyncRelayCommand, awaited and as an operation: an enum failure.
        model.Title = "archived";
        RequireFailure(await driver.ExecuteAsync(vm => vm.PublishCommand), "\"Archived\"");
        var operation = await driver.Start(vm => vm.PublishCommand, requestId: "publish-1").WaitAsync();
        Require(operation.Kind == "domain-failed" && operation.Failure?.GetRawText() == "\"Archived\"",
            $"The Toolkit operation did not end with its declared failure: {operation.Json}");
        model.Title = "ready";
        var published = await driver.Start(vm => vm.PublishCommand, requestId: "publish-2").WaitAsync();
        Require(published.Kind == "succeeded" && model.Title == "published", $"The declared operation did not succeed: {published.Json}");

        // An undeclared operation has no failure codec and succeeds as before.
        Require((await driver.Start(vm => vm.DiscardCommand, requestId: "discard-1").WaitAsync()).Kind == "succeeded",
            "The undeclared operation did not succeed.");

        // Plain ICommand, declared on its property: a DTO failure.
        RequireFailure(await driver.ExecuteAsync(vm => vm.ReserveCommand, "5"), """{"limit":3,"requested":5}""");
        (await driver.ExecuteAsync(vm => vm.ReserveCommand, "2")).EnsureOk();
        Require(model.Title == "reserved 2", "The plain command did not run.");

        // The generated contract fingerprint matches the runtime's reflection of the declarations.
        Require(BridgeContractShape.Compute(typeof(FailureToolkitViewModel)) == ContractFingerprint(host.Transport.Call("failureToolkitStartDiscard",
            new(StringValue: "fingerprint"))), "The generated fingerprint differs from BridgeContractShape for declared failures.");
    }

    private static async Task ReactiveCommandAsync()
    {
        await using var context = new RunicModelContext();
        using var model = new FailureReactiveViewModel(context);
        using var lease = RunicModelContextRegistry.Shared.Bind(context, model);
        using var host = new RunicWindowTestHost<FailureReactiveViewModel>(model, "failureReactive",
            (transport, content, vm) => new FailureReactiveBridge(transport, vm, content: content), new TestViewLocator());
        RequireFailure(await host.Root.ExecuteAsync(vm => vm.SaveCommand), """{"$case":"titleRequired"}""");
        var failed = await host.Root.Start(vm => vm.SaveCommand, requestId: "save-1").WaitAsync();
        Require(failed.Kind == "domain-failed" && failed.Failure?.GetRawText() == """{"$case":"titleRequired"}""",
            $"The ReactiveUI operation did not end with its declared failure: {failed.Json}");
        model.Title = "four";
        var saved = await host.Root.Start(vm => vm.SaveCommand, requestId: "save-2").WaitAsync();
        Require(saved.Kind == "succeeded" && saved.Result?.GetInt32() == 4, $"The ReactiveUI operation did not succeed: {saved.Json}");

        // A stream command keeps the values it published before its declared failure.
        var import = host.Root.Start(vm => vm.ImportCommand, requestId: "import-1");
        var imported = await import.WaitAsync();
        using (var status = JsonDocument.Parse(imported.Json))
            Require(imported.Kind == "domain-failed" && status.RootElement.GetProperty("stream").GetBoolean()
                && imported.Failure?.GetRawText() == """{"$case":"titleTaken","existingTitle":"Todo"}""",
                $"The ReactiveUI stream did not end with its declared failure: {imported.Json}");
        using var page = JsonDocument.Parse(host.Transport.Call("__runicOperationStream", new(StringValue: JsonSerializer.Serialize(new
        {
            contract = import.Contract, member = "Import", requestId = "import-1", cursor = 0,
        }))));
        Require(page.RootElement.GetProperty("kind").GetString() == "domain-failed" && page.RootElement.GetProperty("items").GetArrayLength() == 2,
            $"The stream values were not kept: {page.RootElement}");
    }

    // The generated modules and bridge for the failure fixtures, checked in under
    // golden/. Set RUNIC_UPDATE_GOLDEN=1 to rewrite them after an intended change.
    private static void GoldenOutput()
    {
        var root = FindWorkspaceRoot();
        var project = Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests");
        var generated = Path.Combine(project, "obj", "bridge-frontend", "generated");
        var configuration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(
            typeof(DomainFailureCodegenTests).Assembly)?.Configuration ?? "Debug";
        var bridges = Path.Combine(project, "obj", configuration, "net10.0", "runic-bridge");
        var outputs = new (string Golden, string Actual)[]
        {
            ("failureToolkit.ts", Path.Combine(generated, "failureToolkit.ts")),
            ("failureToolkit.mock.ts", Path.Combine(generated, "failureToolkit.mock.ts")),
            ("failureReactive.ts", Path.Combine(generated, "failureReactive.ts")),
            ("failureReactive.mock.ts", Path.Combine(generated, "failureReactive.mock.ts")),
            ("types.ts", Path.Combine(generated, "types.ts")),
            ("FailureToolkitViewModel.Bridge.g.cs", Path.Combine(bridges, "Runic.Application.Testing.Tests.FailureToolkitViewModel.Bridge.g.cs")),
            ("FailureReactiveViewModel.Bridge.g.cs", Path.Combine(bridges, "Runic.Application.Testing.Tests.FailureReactiveViewModel.Bridge.g.cs")),
        };
        var update = Environment.GetEnvironmentVariable("RUNIC_UPDATE_GOLDEN") == "1";
        foreach (var (golden, actualPath) in outputs)
        {
            // A .golden suffix keeps the files out of the C# and TypeScript builds.
            var goldenPath = Path.Combine(project, "golden", "domain-failures", golden + ".golden");
            // The contract fingerprint covers every View model of the test assembly, so
            // an unrelated fixture would change it; the fingerprint tests cover it.
            var actual = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(actualPath).ReplaceLineEndings("\n"),
                "[0-9A-F]{64}", "<fingerprint>");
            if (update)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
                File.WriteAllText(goldenPath, actual);
                continue;
            }
            Require(File.Exists(goldenPath) && File.ReadAllText(goldenPath).ReplaceLineEndings("\n") == actual,
                $"{golden} differs from the generated {actualPath}. Review the change and run with RUNIC_UPDATE_GOLDEN=1.");
        }
    }

    private static string ContractFingerprint(string admission)
    {
        using var document = JsonDocument.Parse(admission);
        // {ViewModel full name}:{fingerprint}:{route}
        return document.RootElement.GetProperty("contract").GetString()!.Split(':')[1];
    }

    private static void RequireFailure<T>(RunicCallReply<T> reply, string failure) where T : class =>
        Require(reply is { Ok: false, ErrorKind: "domain-failed", State: not null } && reply.Failure?.GetRawText() == failure,
            $"{reply.Route} did not reply with its declared failure {failure}: {reply}");

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Could not locate the Runic SDK workspace root.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
