using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// W130-029 slice 3: declared failures through generated bridges for a sync
// RelayCommand, an AsyncRelayCommand, a plain ICommand and a ReactiveCommand,
// on the awaited route and as operations, plus golden generated output.
internal sealed class DomainFailureCodegenTests
{
    [Test]
    public async Task ToolkitCommandsAsync()
    {
        var model = new FailureToolkitViewModel();
        FailureToolkitBridge? bridge = null;
        using var host = new RunicWindowTestHost<FailureToolkitViewModel>(model,
            (transport, content, vm) => bridge = new FailureToolkitBridge(transport, vm, content: content), new RunicWindowTestHostOptions { RootRoute = "failureToolkit", ViewLocator = new TestViewLocator() });
        var driver = host.Root;

        // Sync RelayCommand, declared on its [RelayCommand] method: a [RunicUnion] failure.
        RequireFailure(await driver.ExecuteAsync(vm => vm.SaveCommand), """{"$case":"titleRequired"}""");
        model.Title = "taken";
        RequireFailure(await driver.ExecuteAsync(vm => vm.SaveCommand), """{"$case":"titleTaken","existingTitle":"Todo"}""");
        model.Title = "fresh";
        (await driver.ExecuteAsync(vm => vm.SaveCommand)).ThrowIfFailed();
        Require(model.Title == "saved", "The undeclared path of a declared command did not run.");

        // AsyncRelayCommand, awaited: an enum failure.
        model.Title = "archived";
        RequireFailure(await driver.ExecuteAsync(vm => vm.PublishCommand), "\"Archived\"");

        // Plain ICommand, declared on its property: a DTO failure.
        RequireFailure(await driver.ExecuteAsync(vm => vm.ReserveCommand, "5"), """{"limit":3,"requested":5}""");
        (await driver.ExecuteAsync(vm => vm.ReserveCommand, "2")).ThrowIfFailed();
        Require(model.Title == "reserved 2", "The plain command did not run.");

        // The generated contract fingerprint matches the runtime's reflection of the declarations.
        Require(BridgeContractShape.Compute(typeof(FailureToolkitViewModel)) == ContractFingerprint(bridge!),
            "The generated fingerprint differs from BridgeContractShape for declared failures.");
    }

    // The AsyncRelayCommand as an operation.
    [Test]
    public async Task ToolkitOperationsAsync()
    {
        var model = new FailureToolkitViewModel();
        using var host = new RunicWindowTestHost<FailureToolkitViewModel>(model,
            (transport, content, vm) => new FailureToolkitBridge(transport, vm, content: content), new RunicWindowTestHostOptions { RootRoute = "failureToolkit", ViewLocator = new TestViewLocator() });
        var driver = host.Root;
        model.Title = "archived";
        var operation = await driver.Start(vm => vm.PublishCommand, requestId: "publish-1").WaitAsync();
        Require(operation.Kind == RunicOperationStatusKind.DomainFailed && operation.Failure?.GetRawText() == "\"Archived\"",
            $"The Toolkit operation did not end with its declared failure: {operation.Json}");
        model.Title = "ready";
        var published = await driver.Start(vm => vm.PublishCommand, requestId: "publish-2").WaitAsync();
        Require(published.Kind == RunicOperationStatusKind.Succeeded && model.Title == "published", $"The declared operation did not succeed: {published.Json}");

        // An undeclared operation has no failure codec and succeeds as before.
        Require((await driver.Start(vm => vm.DiscardCommand, requestId: "discard-1").WaitAsync()).Kind == RunicOperationStatusKind.Succeeded,
            "The undeclared operation did not succeed.");
    }

    [Test]
    public async Task ReactiveCommandAsync()
    {
        await using var context = new RunicModelContext();
        var model = new FailureReactiveViewModel(context);
        using var lease = RunicModelContextRegistry.Shared.Bind(context, model);
        using var host = new RunicWindowTestHost<FailureReactiveViewModel>(model,
            (transport, content, vm) => new FailureReactiveBridge(transport, vm, content: content), new RunicWindowTestHostOptions { RootRoute = "failureReactive", ViewLocator = new TestViewLocator() });
        RequireFailure(await host.Root.ExecuteAsync(vm => vm.SaveCommand), """{"$case":"titleRequired"}""");
    }

    [Test]
    public async Task ReactiveOperationsAsync()
    {
        await using var context = new RunicModelContext();
        var model = new FailureReactiveViewModel(context);
        using var lease = RunicModelContextRegistry.Shared.Bind(context, model);
        using var host = new RunicWindowTestHost<FailureReactiveViewModel>(model,
            (transport, content, vm) => new FailureReactiveBridge(transport, vm, content: content), new RunicWindowTestHostOptions { RootRoute = "failureReactive", ViewLocator = new TestViewLocator() });
        var failed = await host.Root.Start(vm => vm.SaveCommand, requestId: "save-1").WaitAsync();
        Require(failed.Kind == RunicOperationStatusKind.DomainFailed && failed.Failure?.GetRawText() == """{"$case":"titleRequired"}""",
            $"The ReactiveUI operation did not end with its declared failure: {failed.Json}");
        model.Title = "four";
        var saved = await host.Root.Start(vm => vm.SaveCommand, requestId: "save-2").WaitAsync();
        Require(saved.Kind == RunicOperationStatusKind.Succeeded && saved.Result?.GetInt32() == 4, $"The ReactiveUI operation did not succeed: {saved.Json}");

        // A stream command keeps the values it published before its declared failure.
        var import = host.Root.Start(vm => vm.ImportCommand, requestId: "import-1");
        var imported = await import.WaitAsync();
        using (var status = JsonDocument.Parse(imported.Json))
            Require(imported.Kind == RunicOperationStatusKind.DomainFailed && status.RootElement.GetProperty("stream").GetBoolean()
                && imported.Failure?.GetRawText() == """{"$case":"titleTaken","existingTitle":"Todo"}""",
                $"The ReactiveUI stream did not end with its declared failure: {imported.Json}");
        using var reply = JsonDocument.Parse(host.Transport.Send(BridgeRequests.Json(writer =>
        {
            writer.WriteString("kind", "stream");
            writer.WriteString("route", "failureReactive");
            writer.WriteString("member", "import");
            writer.WriteString("operation", "import-1");
            writer.WriteString("contract", import.Contract);
            writer.WriteNumber("cursor", 0);
        })));
        var page = reply.RootElement.GetProperty("page");
        Require(page.GetProperty("kind").GetString() == "domain-failed" && page.GetProperty("items").GetArrayLength() == 2,
            $"The stream values were not kept: {page}");
    }

    // The generated modules and bridges for the failure fixtures and the navigation
    // region slot (W240-003: NavigationRegion<T> now comes from Runic.Navigation),
    // checked in under golden/. Set RUNIC_UPDATE_GOLDEN=1 to rewrite them after an
    // intended change.
    [Test]
    public void GoldenOutput()
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
            ("failureToolkit.contract.d.ts", Path.Combine(generated, "failureToolkit.contract.d.ts")),
            ("failureToolkit.mock.ts", Path.Combine(generated, "failureToolkit.mock.ts")),
            ("failureReactive.ts", Path.Combine(generated, "failureReactive.ts")),
            ("failureReactive.contract.d.ts", Path.Combine(generated, "failureReactive.contract.d.ts")),
            ("failureReactive.mock.ts", Path.Combine(generated, "failureReactive.mock.ts")),
            ("types.ts", Path.Combine(generated, "types.ts")),
            ("FailureToolkitViewModel.Bridge.g.cs", Path.Combine(bridges, "Runic.Application.Testing.Tests.FailureToolkitViewModel.Bridge.g.cs")),
            ("FailureReactiveViewModel.Bridge.g.cs", Path.Combine(bridges, "Runic.Application.Testing.Tests.FailureReactiveViewModel.Bridge.g.cs")),
        };
        CompareGolden(project, "domain-failures", outputs);
        CompareGolden(project, "navigation-region",
        [
            ("navShell.ts", Path.Combine(generated, "navShell.ts")),
            ("navShell.contract.d.ts", Path.Combine(generated, "navShell.contract.d.ts")),
            ("NavShellViewModel.Bridge.g.cs", Path.Combine(bridges, "Runic.Application.Testing.Tests.NavShellViewModel.Bridge.g.cs")),
        ]);
    }

    internal static void CompareGolden(string project, string set, (string Golden, string Actual)[] outputs)
    {
        var update = Environment.GetEnvironmentVariable("RUNIC_UPDATE_GOLDEN") == "1";
        // A Bridge's codecs are in the assembly's shared codecs file: pin the ones the set's Bridges use.
        var bridgeFiles = outputs.Where(output => output.Golden.EndsWith(".Bridge.g.cs", StringComparison.Ordinal)).Select(output => output.Actual).ToArray();
        if (bridgeFiles.Length > 0)
        {
            outputs = [.. outputs, ("shared-codecs.txt", SharedCodecsOf(Path.Combine(project, "obj", $"{set}-shared-codecs.txt"), bridgeFiles))];
            // And the generated command helpers that they call.
            var bridgeText = string.Concat(bridgeFiles.Select(File.ReadAllText));
            foreach (var (helper, file) in new[] { ("RunicToolkitCommands", "RunicShared.Toolkit.Bridge.g.cs"), ("RunicBridgeCommands", "RunicShared.Commands.Bridge.g.cs") })
                if (bridgeText.Contains(helper, StringComparison.Ordinal))
                    outputs = [.. outputs, (file, Path.Combine(Path.GetDirectoryName(bridgeFiles[0])!, file))];
        }
        foreach (var (golden, actualPath) in outputs)
        {
            // A .golden suffix keeps the files out of the C# and TypeScript builds.
            var goldenPath = Path.Combine(project, "golden", set, golden + ".golden");
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

    private const string SharedCodecsFile = "RunicShared.Codecs.Bridge.g.cs";

    // The shared codec classes (BridgeSharedCodecs) that the Bridges name, and those they call in turn, in file order,
    // written to the extract path. Unlike the whole file, it does not change when an unrelated fixture adds a codec.
    private static string SharedCodecsOf(string extract, string[] bridgeFiles)
    {
        var reference = new System.Text.RegularExpressions.Regex(@"RunicBridgeCodecs\.(@?[A-Za-z_][A-Za-z0-9_]*)");
        // Each class is its type comment, its declaration and its body, up to the closing brace at the class's indent.
        var classes = new List<(string Name, string Text)>();
        var lines = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(bridgeFiles[0])!, SharedCodecsFile));
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].StartsWith("    internal static class ", StringComparison.Ordinal)) continue;
            var end = Array.IndexOf(lines, "    }", index);
            classes.Add((lines[index]["    internal static class ".Length..], string.Join('\n', lines[(index - 1)..(end + 1)])));
        }
        var byName = classes.ToDictionary(entry => entry.Name, entry => entry.Text, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(bridgeFiles.SelectMany(path => reference.Matches(File.ReadAllText(path)).Select(match => match.Groups[1].Value)));
        while (pending.TryPop(out var name))
            if (used.Add(name))
                foreach (var match in reference.Matches(byName[name]).Cast<System.Text.RegularExpressions.Match>()) pending.Push(match.Groups[1].Value);
        File.WriteAllText(extract, string.Join("\n\n", classes.Where(entry => used.Contains(entry.Name)).Select(entry => entry.Text)) + "\n");
        return extract;
    }

    // The fingerprint the generated bridge was constructed with.
    private static string? ContractFingerprint(ViewModelBridge bridge) =>
        typeof(ViewModelBridge).GetField("_contractFingerprint", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(bridge) as string;

    private static void RequireFailure<T>(RunicCallReply<T> reply, string failure) where T : class =>
        Require(reply is { Succeeded: false, ErrorKind: "domain-failed", Revision: not null } && reply.Failure?.GetRawText() == failure,
            $"{reply.Route} did not reply with its declared failure {failure}: {reply}");

    internal static string FindWorkspaceRoot()
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
