using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class FormsTests
{
    [Test]
    public void EnumsHaveValueListsAndFieldsCarryTheirDataAnnotations()
    {
        var directory = GeneratedTypeScriptDirectory();
        var types = File.ReadAllText(Path.Combine(directory, "types.ts")).ReplaceLineEndings("\n");
        var forms = File.ReadAllText(Path.Combine(directory, "forms.ts")).ReplaceLineEndings("\n");
        var shape = File.ReadAllText(Path.Combine(directory, "codegenShape.ts")).ReplaceLineEndings("\n");
        foreach (var (file, text, expected) in new[]
        {
            // C8: the names .NET writes, so an alias (Urgent = High) is not listed twice.
            ("types.ts", types, "export type FormsPriority = \"Low\" | \"Normal\" | \"High\" | \"Urgent\";\n"
                + "/** The values of {@link FormsPriority} that .NET writes, in declaration order. */\n"
                + "export const FormsPriorityValues = [\"Low\", \"Normal\", \"High\"] as const satisfies readonly FormsPriority[];"),
            ("types.ts", types, "export const CodegenShapeModeValues = [\"None\", \"Active\"] as const satisfies readonly CodegenShapeMode[];"),
            // Each module re-exports the value lists of the enums it uses, beside their types.
            ("forms.ts", forms, "export { FormsPriorityValues } from \"./types.js\";\n"),
            ("codegenShape.ts", shape, "export { CodegenShapeModeValues } from \"./types.js\";\n"),
            ("forms.ts", forms, "    name: \"name\", kind: \"text\", constraints: { required: true, minLength: 2, maxLength: 40 },\n"),
            ("forms.ts", forms, "    name: \"tag\", kind: \"text\", constraints: { maxLength: 8 },\n"),
            ("forms.ts", forms, "    name: \"quantity\", kind: \"number\", integer: true, constraints: { min: 1, max: 10 },\n"),
            ("forms.ts", forms, "    name: \"price\", kind: \"decimal\", constraints: { min: \"0.01\", max: \"999.99\", minExclusive: true },\n"),
            ("forms.ts", forms, "    name: \"code\", kind: \"text\", constraints: { pattern: \"^[A-Z]{3}$\" },\n"),
            ("forms.ts", forms, "    name: \"loose\", kind: \"text\",\n"),
            ("forms.ts", forms, "    name: \"priority\", kind: \"choice\", choices: [\"Low\", \"Normal\", \"High\"],\n"),
            // C7: an Int64 key is the decimal form of the bigint.
            ("forms.ts", forms, "item => String(item.id))"),
        })
            Require(text.Contains(expected, StringComparison.Ordinal), $"Generated {file} is missing:\n{expected}\n--- {file} ---\n{text}");
    }

    [Test]
    public async Task Int64CollectionKeysTravelAsTheirDecimalFormAsync()
    {
        var model = new FormsViewModel();
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new FormsBridge(transport, model, "forms");
        using (transport.SnapshotState("forms")) { }
        model.Items.Add(new(9_007_199_254_740_993, "beyond 2^53"));
        model.Items.Add(new(-5, "negative"));
        var keys = new List<string?>();
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (keys.Count < 2 && DateTime.UtcNow < timeout)
        {
            foreach (var push in transport.TakePushes("forms"))
                using (push)
                    foreach (var change in push.RootElement.GetProperty("changes").EnumerateArray())
                        if (change.GetProperty("field").GetString() == "rows" && change.GetProperty("kind").GetString() == "add")
                        {
                            keys.AddRange(change.GetProperty("keys").EnumerateArray().Select(key => key.GetString()));
                            Require(change.GetProperty("items")[0].GetProperty("id").ValueKind == JsonValueKind.String,
                                $"An Int64 row field is not a JSON string: {change.GetRawText()}");
                        }
            await Task.Delay(5);
        }
        Require(keys.SequenceEqual(["9007199254740993", "-5"]), $"Int64 keys were not their decimal form: {string.Join(", ", keys)}");
    }

    [Test]
    public async Task PendingValidationIsReportedAndPublishedAsync()
    {
        var model = new PendingFormViewModel();
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new PendingFormBridge(transport, model, "pending");
        using (var initial = transport.SnapshotState("pending"))
        {
            var validation = initial.RootElement.GetProperty("state").GetProperty("validation");
            Require(!validation.TryGetProperty("pending", out _), $"pending is present without a check: {validation.GetRawText()}");
        }

        var check = model.Pending.Begin(nameof(PendingFormViewModel.UserName));
        Require(Pending(await NextValidationAsync(transport)) == "[[\"userName\"]]", "A check that started was not published.");

        // A nested object's own path ("") and an unresolvable one report at the object's path, once.
        using (model.Address.Pending.Begin(nameof(PendingAddress.Postcode)))
        {
            Require(Pending(await NextValidationAsync(transport)) == "[[\"userName\"],[\"address\",\"postcode\"]]",
                "A nested object's check was not reported at its path.");
            using var own = model.Address.Pending.Begin();
            using var unknown = model.Address.Pending.Begin("NoSuchMember");
            var both = await NextValidationAsync(transport, until: value => Pending(value).Contains("[\"address\"]]", StringComparison.Ordinal));
            Require(Pending(both) == "[[\"userName\"],[\"address\"],[\"address\",\"postcode\"]]" || Pending(both) == "[[\"userName\"],[\"address\",\"postcode\"],[\"address\"]]",
                $"The object's own and an unresolvable path were not one path at the object: {Pending(both)}");
        }
        check.Dispose();
        check.Dispose();
        var ended = await NextValidationAsync(transport, until: value => !value.TryGetProperty("pending", out _));
        Require(!ended.GetProperty("hasErrors").GetBoolean(), "Pending validation set hasErrors.");

        var directory = GeneratedTypeScriptDirectory();
        var client = File.ReadAllText(Path.Combine(directory, "pendingForm.ts")).ReplaceLineEndings("\n");
        Require(client.Contains("    validating: state => fieldValidating(state.validation, \"userName\"),\n", StringComparison.Ordinal),
            $"The field descriptor has no validating:\n{client}");
    }

    [Test]
    public void TheTestHostReadsPendingValidation()
    {
        var model = new PendingFormViewModel();
        using var host = new RunicWindowTestHost<PendingFormViewModel>(model,
            (transport, content, vm) => new PendingFormBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "pendingForm", ViewLocator = new TestViewLocator() });
        Require(!host.Root.Snapshot().IsValidating(vm => vm.UserName), "A field without a check is validating.");
        using (model.Pending.Begin(nameof(PendingFormViewModel.UserName)))
        {
            var view = host.Root.Snapshot();
            Require(view.IsValidating(vm => vm.UserName) && !view.IsValidating(vm => vm.Address)
                && view.PendingValidation.Single().SequenceEqual(["userName"]), $"RunicViewState does not read pending: {view}");
        }
        Require(host.Root.Snapshot().PendingValidation.Count == 0, "The check did not end.");
    }

    [Test]
    public async Task TheTrackerCountsOverlappingChecksAndFollowsStreamsAsync()
    {
        var pending = new RunicPendingValidation();
        var changes = new List<string>();
        pending.PropertyChanged += (_, args) => changes.Add(args.PropertyName!);
        var first = pending.Begin("Name");
        var second = pending.Begin("Name");
        first.Dispose();
        Require(pending.IsPending("Name") && pending["Name"] && pending.HasPending, "A path ended while another check of it runs.");
        second.Dispose();
        Require(!pending.HasPending && pending.PendingMemberPaths.Count == 0, "The path stayed pending.");
        Require(changes.Count(change => change == nameof(RunicPendingValidation.HasPending)) == 2, $"Changes: {string.Join(", ", changes)}");

        var gate = new TaskCompletionSource<int>();
        var tracked = pending.Track("Code", () => gate.Task);
        Require(pending.IsPending("Code"), "Track did not mark the path while the check runs.");
        gate.SetResult(7);
        Require(await tracked == 7 && !pending.IsPending("Code"), "Track did not end with the check.");

        var source = new Subject<string>();
        var seen = new List<string>();
        using (source.TrackPending(pending, "Email", state => state == "checking").Subscribe(new Observer(seen.Add)))
        {
            source.OnNext("checking");
            Require(pending.IsPending("Email"), "A pending state did not mark the path.");
            source.OnNext("checking");
            source.OnNext("valid");
            Require(!pending.IsPending("Email"), "A result did not end the mark.");
            source.OnNext("checking");
        }
        Require(!pending.IsPending("Email"), "Disposing the subscription did not end the mark.");
        Require(seen.SequenceEqual(["checking", "checking", "valid", "checking"]), "The operator changed the values.");
    }

    private sealed class Subject<T> : IObservable<T>
    {
        private readonly List<IObserver<T>> _observers = [];
        public IDisposable Subscribe(IObserver<T> observer) { _observers.Add(observer); return new Unsubscribe(() => _observers.Remove(observer)); }
        public void OnNext(T value) { foreach (var observer in _observers.ToArray()) observer.OnNext(value); }
        private sealed class Unsubscribe(Action action) : IDisposable { public void Dispose() => action(); }
    }

    private sealed class Observer(Action<string> next) : IObserver<string>
    {
        public void OnNext(string value) => next(value);
        public void OnError(Exception error) => throw error;
        public void OnCompleted() { }
    }

    private static string Pending(JsonElement validation) =>
        validation.TryGetProperty("pending", out var pending) ? pending.GetRawText() : "";

    // The validation of the next frame that sets it (and satisfies until), or of a state push.
    private static async Task<JsonElement> NextValidationAsync(InMemoryBridgeTransport transport, Func<JsonElement, bool>? until = null, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        var seen = new List<string>();
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < timeout)
        {
            foreach (var push in transport.TakePushes("pending"))
                using (push)
                {
                    var root = push.RootElement;
                    seen.Add(root.GetRawText());
                    JsonElement? validation = root.GetProperty("kind").GetString() == "state"
                        ? root.GetProperty("state").GetProperty("validation")
                        : root.GetProperty("changes").EnumerateArray()
                            .Where(change => change.GetProperty("field").GetString() == "validation")
                            .Select(change => (JsonElement?)change.GetProperty("value")).FirstOrDefault();
                    if (validation is { } value && (until is null || until(value))) return value.Clone();
                }
            await Task.Delay(5);
        }
        throw new InvalidOperationException($"The bridge did not publish validation (line {line}): {string.Join("\n", seen)}");
    }

    private static string GeneratedTypeScriptDirectory()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx")))
                return Path.Combine(directory.FullName, "tests", "dotnet", "Runic.Application.Testing.Tests", "obj", "bridge-frontend", "generated");
        throw new InvalidOperationException("Could not locate the Runic SDK workspace root.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
