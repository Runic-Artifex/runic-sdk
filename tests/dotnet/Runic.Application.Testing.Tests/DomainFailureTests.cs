using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Runic.Application.Testing;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// W130-029: a command's declared failure ([RunicFailure] + RunicFailureException)
// crosses the Bridge as domain-failed with its encoded value. Codegen does not
// emit failure encoders yet, so these descriptors carry a hand-written one.
internal static partial class DomainFailureTests
{
    public static async Task RunAsync()
    {
        var previous = BridgeDiagnostics.IncludeFailureDetail;
        try
        {
            // D-1: a domain failure never carries detail, in either mode.
            foreach (var development in new[] { false, true })
            {
                BridgeDiagnostics.IncludeFailureDetail = development;
                await CommandsReplyWithTheDeclaredFailureAsync();
                await OperationsEndDomainFailedAsync();
                KeyFailureKeepsTheDomainFailure();
            }
            BridgeDiagnostics.IncludeFailureDetail = false;
            await AggregatesAreUnwrappedOnlyWithOneInnerAsync();
            await UndeclaredFailuresStayFailedAsync();
            await CancellationRaceIsDomainFailedAsync();
            await StreamsKeepTheirItemsAsync();
            await RetentionCountsFailuresAsync();
            await TelemetryTreatsDomainFailuresAsExpectedAsync();
            FingerprintCoversOnlyDeclarations();
            FingerprintFollowsDeclarationChanges();
            await PortableFixturesMatchAsync();
        }
        finally
        {
            BridgeDiagnostics.IncludeFailureDetail = previous;
        }
    }

    private static async Task CommandsReplyWithTheDeclaredFailureAsync()
    {
        using var host = Host(new EditorModel(), out var logs);
        // Sync plain ICommand: only a synchronous throw from Execute is seen.
        var sync = await host.Root.ExecuteAsync(vm => vm.SaveCommand);
        RequireDomainReply(sync.Json, "Save", """{"$case":"titleRequired"}""");
        Require(sync is { Ok: false, ErrorKind: "domain-failed", State: not null } && sync.Failure?.GetProperty("$case").GetString() == "titleRequired",
            $"RunicCallReply did not expose the failure: {sync}");

        host.ViewModel.NextFailure = () => new RunicFailureException(new TitleTaken("Todo"));
        var awaited = await host.Root.ExecuteAsync(vm => vm.SubmitCommand);
        RequireDomainReply(awaited.Json, "Submit", """{"$case":"titleTaken","existingTitle":"Todo"}""");

        var entries = logs.Entries.Where(entry => entry.EventId.Id == 1006).ToArray();
        Require(entries.Length == 2 && entries.All(entry => entry.Level == LogLevel.Debug && entry.Exception is RunicFailureException
            && entry.EventId.Name == "BridgeCommandDomainFailed"), $"The domain failures were not logged at Debug with their exception: {Describe(logs)}");
        Require(entries[1].State["FailureType"] as string == typeof(TitleTaken).FullName && entries[1].State["Member"] as string == "Submit",
            $"The domain failure entry was not structured: {string.Join(", ", entries[1].State)}");
        Require(!logs.Entries.Any(entry => entry.Level >= LogLevel.Error), $"A domain failure was logged as an error: {Describe(logs)}");
    }

    private static async Task OperationsEndDomainFailedAsync()
    {
        using var host = Host(new EditorModel(), out var logs);
        host.ViewModel.NextFailure = () => new RunicFailureException(new TitleTaken("Todo"));
        var operation = host.Root.Start(vm => vm.SubmitCommand, requestId: "submit-1");
        var status = await operation.WaitAsync();
        Require(status.Kind == "domain-failed" && status.ErrorMessage is null
            && status.Failure?.GetRawText() == """{"$case":"titleTaken","existingTitle":"Todo"}""",
            $"The operation did not end domain-failed with its failure: {status.Json}");
        using (var json = JsonDocument.Parse(status.Json))
            Require(!json.RootElement.TryGetProperty("error", out _) && !json.RootElement.TryGetProperty("detail", out _)
                && !json.RootElement.TryGetProperty("stream", out _), $"A domain-failed status carried more than its failure: {status.Json}");
        // Recovery and a repeated start observe the same terminal status.
        Require(operation.Status().Json == status.Json, "Status() differed from the waited terminal status.");
        var repeated = host.Root.Start(vm => vm.SubmitCommand, requestId: "submit-1");
        using (var admission = JsonDocument.Parse(repeated.AdmissionJson))
            Require(repeated.Admission == "duplicate" && admission.RootElement.GetProperty("status").GetString() == "domain-failed"
                && admission.RootElement.GetProperty("terminal").GetProperty("failure").GetProperty("$case").GetString() == "titleTaken",
                $"A repeated start did not return the domain-failed terminal: {repeated.AdmissionJson}");
        Require(operation.Cancel() == "not-running", "A domain-failed operation was still cancellable.");
        var entry = logs.Entries.SingleOrDefault(item => item.EventId.Id == 1007)
            ?? throw new InvalidOperationException($"No BridgeOperationDomainFailed entry was logged: {Describe(logs)}");
        Require(entry.Level == LogLevel.Debug && entry.Exception is RunicFailureException && entry.State["Member"] as string == "Submit",
            "The operation domain failure entry was wrong.");
        Require(!logs.Entries.Any(item => item.EventId.Id is 1004 or 1008), $"A domain-failed operation was logged as failed: {Describe(logs)}");
    }

    // D-13: the reply after a command whose state cannot be sent keeps the
    // domain failure, has no state, appends the key message and has no detail.
    private static void KeyFailureKeepsTheDomainFailure()
    {
        var model = new EditorModel();
        using var transport = new InMemoryViewTransport();
        using var bridge = new EditorBridge(transport, model, content: null, keyed: true);
        model.Rows = ["a", "a"];
        using var reply = JsonDocument.Parse(transport.Call("editorSave"));
        var root = reply.RootElement;
        var error = root.GetProperty("error");
        Require(!root.GetProperty("ok").GetBoolean() && root.GetProperty("state").ValueKind == JsonValueKind.Null
            && error.GetProperty("kind").GetString() == "domain-failed"
            && error.GetProperty("message").GetString()!.StartsWith("Save failed. The updated state could not be sent: ", StringComparison.Ordinal)
            && error.GetProperty("failure").GetProperty("$case").GetString() == "titleRequired"
            && !error.TryGetProperty("detail", out _), $"The key failure did not keep the domain failure: {root}");
    }

    private static async Task AggregatesAreUnwrappedOnlyWithOneInnerAsync()
    {
        using var host = Host(new EditorModel(), out var logs);
        host.ViewModel.NextFailure = () => new AggregateException(new AggregateException(new RunicFailureException(new TitleRequired())));
        RequireDomainReply((await host.Root.ExecuteAsync(vm => vm.SaveCommand)).Json, "Save", """{"$case":"titleRequired"}""");
        host.ViewModel.NextFailure = () => new AggregateException(new RunicFailureException(new TitleRequired()), new InvalidOperationException("other"));
        var multiple = await host.Root.ExecuteAsync(vm => vm.SaveCommand);
        Require(multiple.ErrorKind == "failed" && multiple.Failure is null, $"A multi-inner aggregate was not failed: {multiple}");
        Require(logs.Entries.Any(entry => entry.EventId.Id == 1000 && entry.Exception is AggregateException),
            "A multi-inner aggregate was not logged as a failed command.");
        host.ViewModel.NextFailure = () => new AggregateException(new RunicFailureException(new TitleTaken("Todo")));
        var operation = await host.Root.Start(vm => vm.SubmitCommand, requestId: "aggregate").WaitAsync();
        Require(operation.Kind == "domain-failed" && operation.Failure?.GetProperty("existingTitle").GetString() == "Todo",
            $"An operation's single-inner aggregate was not unwrapped: {operation.Json}");
    }

    private static async Task UndeclaredFailuresStayFailedAsync()
    {
        using var host = Host(new EditorModel(), out var logs);
        var huge = new string('x', BridgeDomainFailures.MaximumEncodedBytes);
        var cases = new (string Name, Func<Exception> Failure, string Command, string Reason)[]
        {
            ("undeclared payload", () => new RunicFailureException(new Unrelated("x")), "Save", "the failure is not the declared type"),
            ("no declaration", () => new RunicFailureException(new TitleRequired()), "Undeclared", "the command declares no failure type"),
            ("codec exception", () => new RunicFailureException(new TitleTaken("throw")), "Save", "the failure could not be encoded (InvalidOperationException)"),
            ("over 4 KiB", () => new RunicFailureException(new TitleTaken(huge)), "Save", "the encoded failure exceeds 4096 bytes"),
        };
        foreach (var (name, failure, command, reason) in cases)
        {
            host.ViewModel.NextFailure = failure;
            var reply = command == "Save"
                ? await host.Root.ExecuteAsync(vm => vm.SaveCommand)
                : await host.Root.ExecuteAsync(vm => vm.UndeclaredCommand);
            Require(reply is { ErrorKind: "failed", Failure: null, ErrorMessage: not null } && reply.ErrorMessage == $"{command} failed.",
                $"The {name} did not fall back to failed: {reply}");
            var entry = logs.Entries.LastOrDefault(item => item.EventId.Id == 1008)
                ?? throw new InvalidOperationException($"The {name} was not logged as BridgeDomainFailureNotEncoded.");
            // D-12: an encoder that threw is logged with its own exception beside the RunicFailureException.
            var logged = name == "codec exception"
                ? entry.Exception is AggregateException { InnerExceptions: [RunicFailureException, InvalidOperationException { Message: "The codec failed." }] }
                : entry.Exception is RunicFailureException;
            Require(entry.Level == LogLevel.Error && logged && entry.State["Reason"] as string == reason
                && entry.State["Model"] as string == nameof(EditorModel) && entry.State["Member"] as string == command
                && entry.State["Route"] as string == "editor",
                $"The {name} entry was wrong: {entry.Exception?.GetType().Name} {string.Join(", ", entry.State)}");
        }
        Require(logs.Entries.Count(entry => entry.EventId.Id == 1008) == cases.Length && !logs.Entries.Any(entry => entry.EventId.Id == 1000),
            $"A fallback was logged twice or as BridgeCommandFailed: {Describe(logs)}");

        host.ViewModel.NextFailure = () => new RunicFailureException(new Unrelated("x"));
        var status = await host.Root.Start(vm => vm.SubmitCommand, requestId: "undeclared").WaitAsync();
        Require(status is { Kind: "failed", Failure: null, ErrorMessage: "The operation failed." },
            $"An undeclared operation failure was not failed: {status.Json}");
        Require(logs.Entries.Count(entry => entry.EventId.Id == 1008) == cases.Length + 1 && !logs.Entries.Any(entry => entry.EventId.Id == 1004),
            $"The operation fallback was not logged once as 1008: {Describe(logs)}");
        var operationEntry = logs.Entries.Last(entry => entry.EventId.Id == 1008);
        Require(operationEntry.State["Model"] as string == nameof(EditorModel) && operationEntry.State["Member"] as string == "Submit"
            && operationEntry.State["Route"] as string == "editor" && operationEntry.Exception is RunicFailureException,
            $"The operation fallback entry did not name the model, member and route like a command: {string.Join(", ", operationEntry.State)}");
    }

    // Classified by exception type, like success: a declared failure thrown
    // after cancellation was requested is still domain-failed.
    private static async Task CancellationRaceIsDomainFailedAsync()
    {
        using var host = Host(new EditorModel(), out _);
        var operation = host.Root.Start(vm => vm.RaceCommand, requestId: "race");
        Require(operation.Cancel() == "cancellation-requested", "The race operation was not running.");
        var status = await operation.WaitAsync();
        Require(status.Kind == "domain-failed" && status.Failure?.GetProperty("$case").GetString() == "titleTaken",
            $"A failure thrown during cancellation was not domain-failed: {status.Json}");
    }

    private static async Task StreamsKeepTheirItemsAsync()
    {
        using var host = Host(new EditorModel(), out _);
        var operation = host.Root.Start(vm => vm.PublishCommand, requestId: "stream");
        var status = await operation.WaitAsync();
        using (var json = JsonDocument.Parse(status.Json))
            Require(status.Kind == "domain-failed" && json.RootElement.GetProperty("stream").GetBoolean()
                && status.Failure?.GetProperty("$case").GetString() == "titleRequired",
                $"A stream operation did not end domain-failed with stream: true: {status.Json}");
        var page = host.Transport.Call("__runicOperationStream", new(StringValue: JsonSerializer.Serialize(new
        {
            contract = operation.Contract, member = "Publish", requestId = "stream", cursor = 0,
        })));
        using var stream = JsonDocument.Parse(page);
        Require(stream.RootElement.GetProperty("kind").GetString() == "domain-failed" && stream.RootElement.GetProperty("completed").GetBoolean()
            && stream.RootElement.GetProperty("items").GetArrayLength() == 2
            && stream.RootElement.GetProperty("items")[1].GetProperty("value").GetInt32() == 2,
            $"The published stream items were not readable after the domain failure: {page}");
    }

    private static async Task RetentionCountsFailuresAsync()
    {
        const string failure = """{"$case":"titleTaken","existingTitle":"Todo"}""";
        var bytes = failure.Length;
        using (var registry = new BridgeOperationRegistry("retention", maximumRetainedResultBytes: bytes + bytes / 2))
        {
            var first = await FailAsync(registry, "first", failure);
            Require(first is { Kind: BridgeOperationStatusKind.DomainFailed, DomainFailure: failure, FailureDelivery: null },
                $"The failure was not retained: {first}");
            var second = await FailAsync(registry, "second", failure);
            Require(second.DomainFailure == failure, "The second failure was not retained.");
            // The budget holds one failure, so retaining the second evicted the first.
            Require(registry.Lookup(Request("first").Identity.RegistryKey).Kind is BridgeOperationStatusKind.Expired,
                "The failure bytes were not counted against the retention budget.");
        }
        using (var registry = new BridgeOperationRegistry("retention", maximumRetainedResultBytes: 16))
        {
            var dropped = await FailAsync(registry, "dropped", failure);
            Require(dropped is { Kind: BridgeOperationStatusKind.DomainFailed, DomainFailure: null }
                && dropped.FailureDelivery?.Kind is BridgeOperationDeliveryFailureKind.ResultTooLarge,
                $"A failure over the budget did not stay domain-failed with a delivery: {dropped}");
            var key = Request("dropped").Identity.RegistryKey;
            Require(registry.Lookup(key) == dropped && await registry.WaitForTerminalAsync(key) == dropped,
                "Recovering the dropped failure did not return its terminal status.");
            var wire = TerminalJson(dropped);
            Require(wire["kind"]!.GetValue<string>() == "domain-failed" && wire["failure"] is null
                && wire["delivery"]!["kind"]!.GetValue<string>() == "result-too-large",
                $"The dropped failure was not reported as a delivery: {wire.ToJsonString()}");
        }
    }

    // Telemetry: domain_failed, span status Unset, no error.type, not counted
    // in runic.bridge.failures.
    private static async Task TelemetryTreatsDomainFailuresAsExpectedAsync()
    {
        using var telemetry = new TelemetryCapture();
        using var host = Host(new EditorModel(), out _);
        _ = await host.Root.ExecuteAsync(vm => vm.SaveCommand);
        _ = await host.Root.Start(vm => vm.SubmitCommand, requestId: "telemetry").WaitAsync();
        foreach (var (name, member) in new[] { ("runic.bridge.command", "Save"), ("runic.bridge.operation", "Submit") })
        {
            var activity = telemetry.Activities.SingleOrDefault(item => item.OperationName == name && item.GetTagItem("runic.bridge.member") as string == member)
                ?? throw new InvalidOperationException($"No {name} activity was recorded for {member}.");
            Require(activity.Status == ActivityStatusCode.Unset && activity.GetTagItem("runic.bridge.outcome") as string == "domain_failed"
                && activity.GetTagItem("error.type") is null, $"The {name} span did not report an expected domain failure: {string.Join(", ", activity.TagObjects)}");
            Require(telemetry.Sum("runic.bridge.calls", ("runic.bridge.outcome", "domain_failed"), ("runic.bridge.member", member)) == 1,
                $"runic.bridge.calls did not count the {member} domain failure.");
        }
        Require(telemetry.Sum("runic.bridge.failures", ("runic.bridge.model", nameof(EditorModel))) == 0,
            "runic.bridge.failures counted a domain failure.");
    }

    // Only declared commands add fingerprint lines, so the fingerprints of
    // existing models do not change.
    private static void FingerprintCoversOnlyDeclarations()
    {
        // The parts also cover every View model of the assembly; check the undeclared model's own lines.
        var plain = BridgeContractShape.Parts(typeof(EditorModel)).Where(part => part.Contains(nameof(EditorModel), StringComparison.Ordinal)).ToList();
        Require(!plain.Any(part => part.StartsWith("command-failure:", StringComparison.Ordinal) || part.Contains(".failure:", StringComparison.Ordinal)),
            "An undeclared model gained failure lines.");
        var property = BridgeContractShape.Parts(typeof(PropertyDeclaredModel));
        Require(property.Contains($"command-failure:{typeof(PropertyDeclaredModel).FullName}:SaveCommand:property")
            && property.Any(part => part.StartsWith($"wire:{nameof(PropertyDeclaredModel)}.SaveCommand.failure:type:{typeof(SaveFailure).FullName}", StringComparison.Ordinal)),
            $"A property declaration was not fingerprinted: {string.Join("\n", property)}");
        var method = BridgeContractShape.Parts(typeof(MethodDeclaredModel));
        Require(method.Contains($"command-failure:{typeof(MethodDeclaredModel).FullName}:SaveCommand:method")
            && method.Any(part => part.StartsWith($"wire:{nameof(MethodDeclaredModel)}.SaveCommand.failure:type:{typeof(TitleTaken).FullName}", StringComparison.Ordinal)),
            $"A private [RelayCommand] method declaration was not fingerprinted: {string.Join("\n", method)}");
        var undeclaredMethod = BridgeContractShape.Parts(typeof(MethodUndeclaredModel))
            .Where(part => part.Contains(nameof(MethodUndeclaredModel), StringComparison.Ordinal)).ToList();
        Require(!undeclaredMethod.Any(part => part.StartsWith("command-failure:", StringComparison.Ordinal)),
            "An undeclared [RelayCommand] method gained failure lines.");
    }

    // The same ViewModel, compiled with different declarations, as Hot Reload
    // compares it: a declaration added, moved between the property and its
    // [RelayCommand] method, or given another type changes the fingerprint;
    // the lines of the undeclared command never change.
    private static void FingerprintFollowsDeclarationChanges()
    {
        var variants = new (string Name, string Property, string Method)[]
        {
            ("undeclared", "", ""),
            ("undeclared-again", "", ""),
            ("property", "[RunicFailure(typeof(TitleRequired))]", ""),
            ("property-other-type", "[RunicFailure(typeof(TitleTaken))]", ""),
            ("method", "", "[RunicFailure(typeof(TitleRequired))]"),
        };
        var parts = variants.ToDictionary(variant => variant.Name, variant => FixtureParts(variant.Name, variant.Property, variant.Method));
        static string Hash(List<string> lines) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines))));
        var fingerprints = parts.ToDictionary(pair => pair.Key, pair => Hash(pair.Value));
        Require(fingerprints["undeclared"] == fingerprints["undeclared-again"], "An unchanged model changed its fingerprint.");
        Require(fingerprints.Values.Distinct().Count() == variants.Length - 1,
            $"A changed declaration did not change the fingerprint: {string.Join(", ", fingerprints)}");
        var discard = parts.Values.Select(lines => string.Join('\n', lines.Where(line => line.Contains("DiscardCommand", StringComparison.Ordinal)))).Distinct().ToArray();
        Require(discard.Length == 1 && discard[0].Length > 0, $"The undeclared command's fingerprint lines changed: {string.Join(" | ", discard)}");
    }

    private static List<string> FixtureParts(string name, string property, string method)
    {
        var source = $$"""
            using System.ComponentModel;
            using System.Windows.Input;
            using CommunityToolkit.Mvvm.Input;
            using Runic.Application.Views;
            namespace FingerprintFixture;
            public abstract record SaveFailure;
            public sealed record TitleRequired : SaveFailure;
            public sealed record TitleTaken(string ExistingTitle) : SaveFailure;
            public sealed class EditorViewModel : INotifyPropertyChanged
            {
                {{property}} public ICommand SaveCommand { get; } = new RelayCommand(() => { });
                public ICommand DiscardCommand { get; } = new RelayCommand(() => { });
                {{method}} [RelayCommand] private void Save() { }
                public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create($"FingerprintFixture_{name.Replace('-', '_')}",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Require(emitted.Success, $"The {name} fingerprint fixture did not compile: {string.Join('\n', emitted.Diagnostics)}");
        image.Position = 0;
        // Each variant loads separately so they share one type name; dependencies
        // such as Runic.Application.Views resolve from the default context.
        var context = new System.Runtime.Loader.AssemblyLoadContext(name, isCollectible: true);
        try { return BridgeContractShape.Parts(context.LoadFromStream(image).GetType("FingerprintFixture.EditorViewModel", throwOnError: true)!); }
        finally { context.Unload(); }
    }

    // specs/application/fixtures/domain-failures: the producer writes the wire
    // of each scenario; the views package parses the same files.
    private static async Task PortableFixturesMatchAsync()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "domain-failures");
        var files = Directory.GetFiles(directory, "*.json").OrderBy(file => file, StringComparer.Ordinal).ToArray();
        Require(files.Length == 5, $"The domain failure fixtures were not copied to {directory}.");
        foreach (var file in files)
        {
            var fixture = JsonNode.Parse(File.ReadAllText(file))!;
            var scenario = fixture["scenario"]!.GetValue<string>();
            var expected = fixture["wire"]!;
            JsonNode actual = scenario switch
            {
                "command-reply" => await CommandWireAsync(new TitleTaken("Todo")),
                "undeclared-command-reply" => await CommandWireAsync(new Unrelated("x")),
                "operation-status" => await OperationWireAsync(vm => vm.SubmitCommand, "save-1"),
                "stream-status" => await OperationWireAsync(vm => vm.PublishCommand, "save-1"),
                "retention-dropped-status" => await DroppedWireAsync(),
                _ => throw new InvalidOperationException($"Unknown domain failure fixture scenario {scenario}."),
            };
            Require(JsonNode.DeepEquals(expected, actual),
                $"{Path.GetFileName(file)}: .NET wrote {actual.ToJsonString()}, the fixture expects {expected.ToJsonString()}.");
        }
    }

    private static async Task<JsonNode> DroppedWireAsync()
    {
        using var registry = new BridgeOperationRegistry("fixture", maximumRetainedResultBytes: 16);
        return TerminalJson(await FailAsync(registry, "save-1", """{"$case":"titleTaken","existingTitle":"Todo"}"""));
    }

    private static async Task<JsonNode> CommandWireAsync(object failure)
    {
        using var host = Host(new EditorModel(), out _);
        host.ViewModel.NextFailure = () => new RunicFailureException(failure);
        var reply = await host.Root.ExecuteAsync(vm => vm.SubmitCommand);
        var node = JsonNode.Parse(reply.Json)!.AsObject();
        // The state is the fixture model's; only its presence is portable.
        node["state"] = node["state"] is null ? null : new JsonObject();
        return node;
    }

    private static async Task<JsonNode> OperationWireAsync(System.Linq.Expressions.Expression<Func<EditorModel, object?>> command, string requestId)
    {
        using var host = Host(new EditorModel { NextFailure = () => new RunicFailureException(new TitleTaken("Todo")) }, out _);
        var status = await host.Root.Start(command, requestId: requestId).WaitAsync();
        var node = JsonNode.Parse(status.Json)!.AsObject();
        node["contract"] = "Tests.Editor:fixture:editor";
        return node;
    }

    private static async Task<BridgeOperationStatus> FailAsync(BridgeOperationRegistry registry, string requestId, string failure)
    {
        var request = Request(requestId);
        _ = registry.Accept(request, () => true,
            _ => Task.FromException<BridgeOperationResult>(new BridgeDomainFailedException(failure, typeof(TitleTaken).FullName!,
                new RunicFailureException(new TitleTaken("Todo")))));
        return await registry.WaitForTerminalAsync(request.Identity.RegistryKey);
    }

    private static BridgeOperationRequest Request(string requestId) =>
        BridgeOperationRequest.Create("Tests.Editor:fixture:editor", "Save", requestId, "digest");

    private static JsonNode TerminalJson(BridgeOperationStatus status)
    {
        // The registry key ends with the request id, which has no colon.
        var identity = BridgeOperationIdentity.Create("Tests.Editor:fixture:editor", status.RequestId.Split(':').Last());
        var admission = JsonNode.Parse(BridgeOperationRouter.EncodeAdmission(new BridgeOperationAdmissionReply(identity,
            BridgeOperationAdmissionKind.Duplicate, status.Kind, null, status)))!;
        return admission["terminal"]!.DeepClone();
    }

    private static void RequireDomainReply(string json, string member, string failure)
    {
        using var reply = JsonDocument.Parse(json);
        var root = reply.RootElement;
        var error = root.GetProperty("error");
        Require(!root.GetProperty("ok").GetBoolean() && root.GetProperty("state").ValueKind == JsonValueKind.Object
            && error.GetProperty("kind").GetString() == "domain-failed" && error.GetProperty("message").GetString() == $"{member} failed."
            && error.GetProperty("failure").GetRawText() == failure && !error.TryGetProperty("detail", out _)
            && !root.TryGetProperty("protocol", out _),
            $"{member} did not reply with its declared failure: {json}");
    }

    private static RunicWindowTestHost<EditorModel> Host(EditorModel model, out LogCapture logs)
    {
        logs = new LogCapture();
        return new RunicWindowTestHost<EditorModel>(model, (transport, content, vm) => new EditorBridge(transport, vm, content),
            new RunicWindowTestHostOptions { RootRoute = "editor", LoggerFactory = logs, ViewLocator = new TestViewLocator() });
    }

    private static string Describe(LogCapture logs) =>
        string.Join(", ", logs.Entries.Select(entry => $"{entry.EventId.Id}/{entry.Level}"));

    private abstract record SaveFailure;
    private sealed record TitleRequired : SaveFailure;
    private sealed record TitleTaken(string ExistingTitle) : SaveFailure;
    private sealed record Unrelated(string Value);

    // The encoder codegen will emit: the declared type's JSON, or null.
    private static string? Encode(object failure) => failure switch
    {
        TitleRequired => """{"$case":"titleRequired"}""",
        TitleTaken { ExistingTitle: "throw" } => throw new InvalidOperationException("The codec failed."),
        TitleTaken taken => new JsonObject { ["$case"] = "titleTaken", ["existingTitle"] = taken.ExistingTitle }.ToJsonString(),
        _ => null,
    };

    private sealed class EditorModel : INotifyPropertyChanged
    {
        public EditorModel()
        {
            SaveCommand = new DelegateCommand(() => throw NextFailure());
            UndeclaredCommand = new DelegateCommand(() => throw NextFailure());
            SubmitCommand = new DelegateCommand(() => { });
            RaceCommand = new DelegateCommand(() => { });
            PublishCommand = new DelegateCommand(() => { });
        }

        [RunicIgnore] public Func<Exception> NextFailure { get; set; } = () => new RunicFailureException(new TitleRequired());
        [RunicIgnore] public List<string> Rows { get; set; } = ["a"];
        public string Title { get; set; } = "";
        public ICommand SaveCommand { get; }
        public ICommand UndeclaredCommand { get; }
        public ICommand SubmitCommand { get; }
        public ICommand RaceCommand { get; }
        public ICommand PublishCommand { get; }

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

        public async Task SubmitAsync()
        {
            await Task.Yield();
            throw NextFailure();
        }

        public static async Task RaceAsync(CancellationToken token)
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
            throw new RunicFailureException(new TitleTaken("Todo"));
        }

        public async Task<BridgeOperationResult> PublishAsync(BridgeOperationExecution execution)
        {
            execution.Stream!.TryPublish("1");
            execution.Stream.TryPublish("2");
            await Task.Yield();
            throw NextFailure();
        }
    }

    private sealed class EditorBridge(IBridgeTransport transport, EditorModel model, WindowContentSession? content, bool keyed = false)
        : ViewModelBridge<EditorModel>(transport, model, "editor", (writer, vm, revision) =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("revision", revision);
            writer.WriteString("title", vm.Title);
            writer.WriteEndObject();
        },
        [],
        [
            new("Save", vm => vm.SaveCommand, EncodeFailure: Encode),
            new("Undeclared", vm => vm.UndeclaredCommand),
            new("Submit", vm => vm.SubmitCommand, ExecuteAsync: (vm, _, _) => vm.SubmitAsync(), EncodeFailure: Encode),
            new("Race", vm => vm.RaceCommand, ExecuteAsync: (_, token, _) => EditorModel.RaceAsync(token), EncodeFailure: Encode),
            new("Publish", vm => vm.PublishCommand, CreateStream: () => new BridgeOperationStream(),
                ExecuteStreamAsync: (vm, execution, _, _) => vm.PublishAsync(execution), EncodeFailure: Encode),
        ],
        BridgeContractShape.Compute(typeof(EditorModel)), content,
        collections: keyed
            ? [new("rows", vm => vm.Rows, (writer, item) => writer.WriteStringValue((string)item!), item => (string)item!, PublishesChanges: false)]
            : null);

    private sealed class PropertyDeclaredModel : INotifyPropertyChanged
    {
        [RunicFailure(typeof(SaveFailure))] public ICommand SaveCommand { get; } = new DelegateCommand(() => { });
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    private sealed partial class MethodDeclaredModel : ObservableObject
    {
        [RelayCommand, RunicFailure(typeof(TitleTaken))]
        private static Task SaveAsync() => Task.CompletedTask;
    }

    private sealed partial class MethodUndeclaredModel : ObservableObject
    {
        [RelayCommand]
        private static Task SaveAsync() => Task.CompletedTask;
    }

    private sealed class DelegateCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, Exception? Exception, IReadOnlyDictionary<string, object?> State);

    private sealed class LogCapture : ILoggerFactory
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose()
        {
        }

        private sealed class Logger(LogCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
                owner.Entries.Enqueue(new(logLevel, eventId, exception, values.ToDictionary(pair => pair.Key, pair => pair.Value)));
            }
        }
    }

    private sealed class TelemetryCapture : IDisposable
    {
        private readonly ActivityListener _activities;
        private readonly MeterListener _meters = new();
        private readonly ConcurrentQueue<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> _measurements = new();

        public TelemetryCapture()
        {
            _activities = new ActivityListener
            {
                ShouldListenTo = source => source.Name == RunicViewsTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = Activities.Enqueue,
            };
            ActivitySource.AddActivityListener(_activities);
            _meters.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RunicViewsTelemetry.MeterName) listener.EnableMeasurementEvents(instrument);
            };
            _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) => _measurements.Enqueue((instrument.Name, value, tags.ToArray())));
            _meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) => _measurements.Enqueue((instrument.Name, value, tags.ToArray())));
            _meters.Start();
        }

        public ConcurrentQueue<Activity> Activities { get; } = new();

        public double Sum(string instrument, params (string Key, string? Value)[] tags) =>
            _measurements.Where(measurement => measurement.Name == instrument && tags.All(tag =>
                measurement.Tags.Any(pair => pair.Key == tag.Key && Equals(pair.Value as string, tag.Value)))).Sum(measurement => measurement.Value);

        public void Dispose()
        {
            _activities.Dispose();
            _meters.Dispose();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
