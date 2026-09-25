using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

string root = FindRoot();
string fixture = Path.Combine(root, "tests", "fixtures", "application", "PostMvvmDiscovery", "PostMvvmDiscovery.csproj");
string smoke = Path.Combine(root, "tests", "fixtures", "application", "PostMvvmDiscovery", "Smoke", "PostMvvmDiscovery.Smoke.csproj");
const string outputKey = "ordinary";
string serialOwner = Guid.NewGuid().ToString("N");
var observedDiagnostics = new Dictionary<string, string>(StringComparer.Ordinal);
RestoreFixture(serialOwner);
VerifyUnwrappedFixtureOperationsAreRejected();
VerifyUnsafeOutputKeysAreRejectedBeforeDeletion();
VerifyExplicitOutputOverridesAreRejectedBeforeFixtureWork();
foreach (string configuration in new[] { "Debug", "Release" })
{
    VerifyRejectedBinding(configuration, "POST_MVVM_OPEN_GENERIC", "RUNICPM001", "GenericNotesView`1");
    VerifyRejectedBinding(configuration, "POST_MVVM_DUPLICATE_VIEW", "RUNICPM002", "AnotherNotesView, Runic.Application.Bridge.PostMvvmFixture.NotesView");
    VerifyRejectedBinding(configuration, "POST_MVVM_MISMATCH", "RUNICPM003", "OtherViewModel");
    VerifyRejectedBinding(configuration, "POST_MVVM_EXTERNAL_MODEL", "RUNICPM004", "System.String");
    VerifyRejectedBinding(configuration, "POST_MVVM_MAPPING_UNMAPPED", "RUNICPM007", "HistoryViewModel");
    VerifyRejectedBinding(configuration, "POST_MVVM_MAPPING_DUPLICATE_KEY", "RUNICPM005", "duplicate selection keys");
    VerifyRejectedBinding(configuration, "POST_MVVM_MAPPING_INCOMPATIBLE", "RUNICPM006", "OtherViewModel");
}
ArtifactSet singleMappedDebugArtifacts = VerifyMappedInterfaceBinding("Debug", multiple: false);
ArtifactSet singleMappedReleaseArtifacts = VerifyMappedInterfaceBinding("Release", multiple: false);
if (singleMappedDebugArtifacts != singleMappedReleaseArtifacts)
    throw new InvalidOperationException("Debug and Release single-model interface mapping bytes differ.");
ArtifactSet mappedDebugArtifacts = VerifyMappedInterfaceBinding("Debug", multiple: true);
ArtifactSet mappedReleaseArtifacts = VerifyMappedInterfaceBinding("Release", multiple: true);
if (mappedDebugArtifacts != mappedReleaseArtifacts)
    throw new InvalidOperationException("Debug and Release explicit interface mapping bytes differ.");
AssertStableDeclaredViewSurface(singleMappedDebugArtifacts.Ir, mappedDebugArtifacts.Ir);
ArtifactSet interfaceDebugArtifacts = VerifyInterfaceViewBinding("Debug");
ArtifactSet interfaceReleaseArtifacts = VerifyInterfaceViewBinding("Release");
if (interfaceDebugArtifacts != interfaceReleaseArtifacts)
    throw new InvalidOperationException("Debug and Release interface View discovery bytes differ.");
ArtifactSet debugArtifacts = VerifyConfiguration("Debug");
ArtifactSet releaseArtifacts = VerifyConfiguration("Release");
if (debugArtifacts != releaseArtifacts)
    throw new InvalidOperationException("Debug and Release post-MVVM IR, ESM, adapter, or ready manifest bytes differ.");
VerifyConcurrentSameKeyBuildOwnership();
foreach (string configuration in new[] { "Debug", "Release" })
{
    string output = RunDotnet("run", "--project", smoke, "-c", configuration, "--no-launch-profile");
    Equal(1, Count(output, "POST_MVVM_SDK_TYPED_ACCESSOR_OK"), $"The {configuration} typed fixture accessor did not run.");
}
Console.WriteLine("POST_MVVM_SDK_EMISSION_OK|toolkit-generated-title|toolkit-save-command|reactive-refresh-command|typed-accessors-run|explicit-window-view|one-and-two-key-interface-mapping|stable-declared-title-routes|declared-contract-only|deterministic-ir-esm-adapter-ready|binding-diagnostics|metadata-nonexecution|same-key-concurrent-owner-isolation|bootstrap=1|outer=1");

void VerifyUnwrappedFixtureOperationsAreRejected()
{
    foreach (string operation in new[] { "restore", "build", "clean" })
    {
        string[] command = operation == "build"
            ? [operation, fixture, "-c", "Debug", "--nologo", "--no-restore"]
            : operation == "clean"
                ? [operation, fixture, "-c", "Debug", "--nologo"]
                : [operation, fixture, "--nologo"];
        ProcessResult result = ExecuteRawDotnet(command);
        if (result.ExitCode == 0 || !result.Output.Contains("RUNICPM009", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unwrapped post-MVVM {operation} did not explain that a build owner is required:\n{result.Output}");
    }
    Console.WriteLine("POST_MVVM_SDK_BUILD_OWNER_REQUIRED|restore|build|clean");
}

void VerifyUnsafeOutputKeysAreRejectedBeforeDeletion()
{
    string applicationDirectory = Path.Combine(root, "tests", "fixtures", "application");
    string escapeName = "post-mvvm-output-key-sentinel-" + Guid.NewGuid().ToString("N");
    string escapeDirectory = Path.Combine(applicationDirectory, escapeName, "Debug", "net10.0");
    string sentinel = Path.Combine(escapeDirectory, "must-survive.txt");
    Directory.CreateDirectory(escapeDirectory);
    File.WriteAllText(sentinel, "outside generated discovery output");
    try
    {
        // `%3B` reaches MSBuild as a literal item-list separator; a raw `;`
        // is rejected by the dotnet command-line property parser before MSBuild
        // evaluates this project.
        foreach (string unsafeKey in new[] { "", "..", "../../../" + escapeName, "has/separator", "has%3Bitem-list" })
        {
            foreach (string operation in new[] { "restore", "build", "clean" })
            {
                string[] command = operation == "build"
                    ? [operation, fixture, "-c", "Debug", "--nologo", "--no-restore", "-p:RunicPostMvvmDiscoveryOutputKey=" + unsafeKey]
                    : operation == "clean"
                        ? [operation, fixture, "-c", "Debug", "--nologo", "-p:RunicPostMvvmDiscoveryOutputKey=" + unsafeKey]
                        : [operation, fixture, "--nologo", "-p:RunicPostMvvmDiscoveryOutputKey=" + unsafeKey];
                ProcessResult result = ExecuteDotnet(command);
                if (result.ExitCode == 0 || !result.Output.Contains("RUNICPM008", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Unsafe output key '{unsafeKey}' did not fail {operation} validation before deletion:\n{result.Output}");
                if (!File.Exists(sentinel))
                    throw new InvalidOperationException($"Unsafe output key '{unsafeKey}' removed content outside the generated discovery output.");
            }
        }
        Console.WriteLine("POST_MVVM_SDK_OUTPUT_KEY_REJECTED|restore|build|clean|empty|traversal|separator|item-list|outside-sentinel-retained");
    }
    finally
    {
        string escapeRoot = Path.Combine(applicationDirectory, escapeName);
        if (Directory.Exists(escapeRoot)) Directory.Delete(escapeRoot, recursive: true);
    }
}

void VerifyExplicitOutputOverridesAreRejectedBeforeFixtureWork()
{
    string escapeDirectory = Path.Combine(Path.GetTempPath(), "runic-post-mvvm-output-override-" + Guid.NewGuid().ToString("N"));
    string sentinel = Path.Combine(escapeDirectory, "must-survive.txt");
    const string sentinelContents = "outside build-owner output root";
    Directory.CreateDirectory(escapeDirectory);
    File.WriteAllText(sentinel, sentinelContents);
    try
    {
        foreach (string property in new[] { "OutputPath", "IntermediateOutputPath" })
        {
            ProcessResult result = ExecuteFixtureWrapper("-p:" + property + "=" + escapeDirectory + Path.DirectorySeparatorChar);
            if (result.ExitCode == 0 || !result.Output.Contains("RUNICPM010", StringComparison.Ordinal))
                throw new InvalidOperationException($"Global {property} did not fail in the top-level owner driver before fixture work:\n{result.Output}");
            string[] entries = Directory.EnumerateFileSystemEntries(escapeDirectory, "*", SearchOption.AllDirectories)
                .Select(entry => Path.GetRelativePath(escapeDirectory, entry))
                .OrderBy(entry => entry, StringComparer.Ordinal)
                .ToArray();
            if (!entries.SequenceEqual(["must-survive.txt"], StringComparer.Ordinal) || File.ReadAllText(sentinel) != sentinelContents)
                throw new InvalidOperationException($"Global {property} wrote to the attempted shared output directory before the owner driver rejected it.");
        }
        Console.WriteLine("POST_MVVM_SDK_OUTPUT_OVERRIDE_REJECTED|top-level-owner-driver-before-restore-build|output-and-intermediate-path|outside-sentinel-retained");
    }
    finally
    {
        if (Directory.Exists(escapeDirectory)) Directory.Delete(escapeDirectory, recursive: true);
    }
}

ArtifactSet VerifyMappedInterfaceBinding(string configuration, bool multiple)
{
    string sentinel = Artifact(configuration, "module-initializer-sentinel.txt");
    Environment.SetEnvironmentVariable("RUNIC_POST_MVVM_SENTINEL", sentinel);
    try
    {
        CleanFixture(serialOwner, configuration);
        if (File.Exists(sentinel)) File.Delete(sentinel);

        string scenario = multiple ? "POST_MVVM_INTERFACE_MULTIPLE_MODELS" : "POST_MVVM_INTERFACE_SINGLE_MAPPING";
        string firstBuild = RunDotnet("build", fixture, "-c", configuration, "--nologo", "--no-restore", $"-p:DefineConstants={scenario}");
        Equal(1, Count(firstBuild, "POST_MVVM_SDK_DISCOVERY_TARGET"), "The mapped View build did not invoke exactly one discovery target.");
        ArtifactSet first = VerifyMappedArtifacts(configuration, sentinel, multiple);

        string rebuild = RunDotnet("build", fixture, "-c", configuration, "--nologo", "--no-restore", $"-p:DefineConstants={scenario}");
        Equal(1, Count(rebuild, "POST_MVVM_SDK_DISCOVERY_TARGET"), "The mapped View rebuild did not invoke exactly one discovery target.");
        ArtifactSet second = VerifyMappedArtifacts(configuration, sentinel, multiple);
        if (first != second)
            throw new InvalidOperationException($"{configuration} mapped View discovery bytes changed on rebuild.");
        Console.WriteLine($"POST_MVVM_SDK_MAPPED_VIEW_OK|{configuration}|{(multiple ? "two-selection-keys" : "one-selection-key")}|declared-title-only|no-command-union");
        return second;
    }
    finally
    {
        Environment.SetEnvironmentVariable("RUNIC_POST_MVVM_SENTINEL", null);
        if (File.Exists(sentinel)) File.Delete(sentinel);
    }
}

ArtifactSet VerifyMappedArtifacts(string configuration, string sentinel, bool multiple)
{
    string directory = Path.GetDirectoryName(Artifact(configuration, "view-bridge.ir.json"))!;
    string[] stages = File.ReadAllLines(Path.Combine(directory, "stages.log")).Where(line => !String.IsNullOrWhiteSpace(line)).ToArray();
    if (!stages.SequenceEqual(["bootstrap", "outer"], StringComparer.Ordinal))
        throw new InvalidOperationException("The mapped View did not use the two-stage compiled discovery path.");
    string ir = File.ReadAllText(Path.Combine(directory, "view-bridge.ir.json"));
    string esm = File.ReadAllText(Path.Combine(directory, "view-bridge.contract.ts"));
    string adapter = File.ReadAllText(Path.Combine(directory, "PostMvvmDiscoveryAdapter.g.cs"));
    string ready = File.ReadAllText(Path.Combine(directory, "view-bridge.ready.json"));
    using JsonDocument document = JsonDocument.Parse(ir);
    JsonElement rootElement = document.RootElement;
    const string view = "Runic.Application.Bridge.PostMvvmFixture.NotesView";
    const string context = "Runic.Application.Bridge.PostMvvmFixture.IEditorContract";
    const string history = "Runic.Application.Bridge.PostMvvmFixture.HistoryViewModel";
    const string notes = "Runic.Application.Bridge.PostMvvmFixture.NotesViewModel";
    JsonElement[] bindings = rootElement.GetProperty("bindings").EnumerateArray().ToArray();
    if (bindings.Length != (multiple ? 2 : 1) ||
        (multiple && (bindings[0].GetProperty("selectionKey").GetString() != "history-editor" || bindings[0].GetProperty("concreteModel").GetString() != history)) ||
        bindings[^1].GetProperty("selectionKey").GetString() != "local-editor" || bindings[^1].GetProperty("concreteModel").GetString() != notes ||
        bindings.Any(binding => binding.GetProperty("view").GetString() != view || binding.GetProperty("declaredContext").GetString() != context))
        throw new InvalidOperationException("The compiled View mapping lost its explicit application selection keys.");
    JsonElement contract = rootElement.GetProperty("contracts").EnumerateArray().Single();
    JsonElement field = contract.GetProperty("fields").EnumerateArray().Single();
    if (contract.GetProperty("view").GetString() != view || contract.GetProperty("declaredContext").GetString() != context ||
        field.GetProperty("name").GetString() != "title" || !field.GetProperty("writable").GetBoolean() ||
        contract.GetProperty("commands").GetArrayLength() != 0)
        throw new InvalidOperationException("The mapped View contract was widened beyond the declared interface.");
    JsonElement[] models = rootElement.GetProperty("models").EnumerateArray().ToArray();
    if (models.Length != (multiple ? 2 : 1) ||
        (multiple && (models[0].GetProperty("name").GetString() != history ||
            !models[0].GetProperty("fields").EnumerateArray().Any(candidate => candidate.GetProperty("name").GetString() == "historyOnly"))) ||
        models[^1].GetProperty("name").GetString() != notes ||
        !models[^1].GetProperty("commands").EnumerateArray().Any(candidate => candidate.GetProperty("name").GetString() == "saveCommand"))
        throw new InvalidOperationException("The concrete model metadata was lost or mixed with the interface contract.");
    JsonElement runtime = rootElement.GetProperty("runtime");
    JsonElement[] routes = runtime.GetProperty("routes").EnumerateArray().ToArray();
    if (runtime.GetProperty("referencePrefix").GetString() != "mounted-reference-prefix" || routes.Length != 2 ||
        routes[0].GetProperty("id").GetString() != "titleRead" || routes[0].GetProperty("model").GetString() != context ||
        routes[0].GetProperty("suffix").GetString() != ".title.read" ||
        routes[1].GetProperty("id").GetString() != "titleWrite" || routes[1].GetProperty("model").GetString() != context ||
        routes[1].GetProperty("suffix").GetString() != ".title.write" ||
        routes.Any(route => route.GetProperty("request").GetProperty("fields").EnumerateArray().Any(item => item.GetProperty("name").GetString() == "historyOnly")))
        throw new InvalidOperationException("The mapped runtime did not declare only the shared Title routes.");
    string fingerprint = rootElement.GetProperty("fingerprint").GetString()!;
    using JsonDocument readyDocument = JsonDocument.Parse(ready);
    if (readyDocument.RootElement.GetProperty("fingerprint").GetString() != fingerprint ||
        !esm.Contains($"export const fingerprint = \"{fingerprint}\";", StringComparison.Ordinal) ||
        !adapter.Contains($"public const string Fingerprint = \"{fingerprint}\";", StringComparison.Ordinal) ||
        !esm.Contains("readonly title: string;", StringComparison.Ordinal) ||
        !esm.Contains("export const commands = [] as const;", StringComparison.Ordinal) ||
        !esm.Contains("export const runtime = ", StringComparison.Ordinal) ||
        !esm.Contains("export function encodeTitleRequest", StringComparison.Ordinal) ||
        !esm.Contains("export function decodeTitleReply", StringComparison.Ordinal) ||
        esm.Contains("historyOnly", StringComparison.Ordinal) || esm.Contains("saveCommand", StringComparison.Ordinal) ||
        esm.Contains("refreshCommand", StringComparison.Ordinal) ||
        (multiple && !adapter.Contains("ReadTitle(global::" + history, StringComparison.Ordinal)) ||
        !adapter.Contains("ReadTitle(global::" + notes, StringComparison.Ordinal) ||
        !adapter.Contains("AttachMappedFixtureRoutes(", StringComparison.Ordinal) ||
        !adapter.Contains("session.HasPresentation(getReference(), arguments.Connection", StringComparison.Ordinal) ||
        adapter.Contains("InvokeSaveCommand", StringComparison.Ordinal) ||
        adapter.Contains("HistoryOnly", StringComparison.Ordinal))
        throw new InvalidOperationException("The mapped ESM or adapter exposed a concrete-only member or lost its fingerprint.");
    if (File.Exists(sentinel)) throw new InvalidOperationException("Mapped metadata inspection executed the fixture module initializer.");
    return new ArtifactSet(ir, esm, adapter, ready);
}

void AssertStableDeclaredViewSurface(string oneWindowIr, string twoWindowIr)
{
    using JsonDocument single = JsonDocument.Parse(oneWindowIr);
    using JsonDocument multiple = JsonDocument.Parse(twoWindowIr);
    JsonElement one = single.RootElement;
    JsonElement two = multiple.RootElement;
    if (one.GetProperty("contracts").GetRawText() != two.GetProperty("contracts").GetRawText() ||
        one.GetProperty("runtime").GetProperty("routes").GetRawText() !=
            two.GetProperty("runtime").GetProperty("routes").GetRawText())
        throw new InvalidOperationException("Adding a second mapped model changed the declared View contract or Title route surface.");
}

ArtifactSet VerifyInterfaceViewBinding(string configuration)
{
    string sentinel = Artifact(configuration, "module-initializer-sentinel.txt");
    Environment.SetEnvironmentVariable("RUNIC_POST_MVVM_SENTINEL", sentinel);
    try
    {
        CleanFixture(serialOwner, configuration);
        if (File.Exists(sentinel)) File.Delete(sentinel);

        string firstBuild = RunDotnet("build", fixture, "-c", configuration, "--nologo", "--no-restore", "-p:DefineConstants=POST_MVVM_INTERFACE_VIEW");
        Equal(1, Count(firstBuild, "POST_MVVM_SDK_DISCOVERY_TARGET"), "The interface View outer build did not invoke exactly one discovery target.");
        ArtifactSet first = VerifyArtifacts(configuration, sentinel);
        VerifyInterfaceViewContract(first.Ir);

        string rebuild = RunDotnet("build", fixture, "-c", configuration, "--nologo", "--no-restore", "-p:DefineConstants=POST_MVVM_INTERFACE_VIEW");
        Equal(1, Count(rebuild, "POST_MVVM_SDK_DISCOVERY_TARGET"), "The interface View rebuild did not invoke exactly one discovery target.");
        ArtifactSet second = VerifyArtifacts(configuration, sentinel);
        VerifyInterfaceViewContract(second.Ir);
        if (first != second)
            throw new InvalidOperationException($"{configuration} interface View discovery bytes changed on a rebuild.");
        Console.WriteLine($"POST_MVVM_SDK_INTERFACE_VIEW_OK|{configuration}|declared-contract|concrete-model|no-member-union");
        return second;
    }
    finally
    {
        Environment.SetEnvironmentVariable("RUNIC_POST_MVVM_SENTINEL", null);
        if (File.Exists(sentinel)) File.Delete(sentinel);
    }
}

void VerifyInterfaceViewContract(string ir)
{
    using JsonDocument document = JsonDocument.Parse(ir);
    JsonElement rootElement = document.RootElement;
    JsonElement[] views = rootElement.GetProperty("views").EnumerateArray().ToArray();
    JsonElement window = views.Single(view => view.GetProperty("kind").GetString() == "window");
    JsonElement view = views.Single(candidate => candidate.GetProperty("kind").GetString() == "view");
    const string model = "Runic.Application.Bridge.PostMvvmFixture.NotesViewModel";
    const string contract = "Runic.Application.Bridge.PostMvvmFixture.IEditorContract";
    if (window.GetProperty("declaredContext").GetString() != model || window.GetProperty("concreteModel").GetString() != model ||
        view.GetProperty("declaredContext").GetString() != contract || view.GetProperty("concreteModel").GetString() != model)
        throw new InvalidOperationException("The interface View IR did not retain its declared context and selected concrete model.");

    JsonElement[] models = rootElement.GetProperty("models").EnumerateArray().ToArray();
    if (models.Length != 1 || models[0].GetProperty("name").GetString() != model ||
        models[0].GetProperty("fields").EnumerateArray().Any(field => field.GetProperty("name").GetString() == "iEditorContract") ||
        !models[0].GetProperty("fields").EnumerateArray().Any(field => field.GetProperty("name").GetString() == "title" && field.GetProperty("writable").GetBoolean()))
        throw new InvalidOperationException("The interface View inspection widened or restricted the concrete model member surface.");
}

void VerifyRejectedBinding(string configuration, string scenario, string expectedCode, string expectedDetail)
{
    string sentinel = Artifact(configuration, "module-initializer-sentinel.txt");
    Environment.SetEnvironmentVariable("RUNIC_POST_MVVM_SENTINEL", sentinel);
    try
    {
        CleanFixture(serialOwner, configuration);
        if (File.Exists(sentinel)) File.Delete(sentinel);
        ProcessResult result = ExecuteDotnet("build", fixture, "-c", configuration, "--nologo", "--no-restore", $"-p:DefineConstants={scenario}");
        if (result.ExitCode == 0) throw new InvalidOperationException($"{configuration} {scenario} unexpectedly built.");
        Match diagnostic = Regex.Match(result.Output, @"RUNICPM\d{3}: [^\r\n]*");
        if (!diagnostic.Success || !diagnostic.Value.StartsWith(expectedCode + ": ", StringComparison.Ordinal) ||
            !diagnostic.Value.Contains(expectedDetail, StringComparison.Ordinal))
            throw new InvalidOperationException($"{configuration} {scenario} did not produce its actionable diagnostic:\n{result.Output}");
        if (Regex.Count(result.Output, @"RUNICPM\d{3}: ") != 1)
            throw new InvalidOperationException($"{configuration} {scenario} emitted more than one fixture diagnostic:\n{result.Output}");
        string message = diagnostic.Value;
        if (observedDiagnostics.TryGetValue(scenario, out string? previous) && !String.Equals(previous, message, StringComparison.Ordinal))
            throw new InvalidOperationException($"{scenario} diagnostic changed between Debug and Release. Debug: {previous} Release: {message}");
        observedDiagnostics[scenario] = message;
        foreach (string file in new[] { "view-bridge.ir.json", "view-bridge.contract.ts", "PostMvvmDiscoveryAdapter.g.cs", "view-bridge.ready.json" })
            if (File.Exists(Artifact(configuration, file)))
                throw new InvalidOperationException($"{configuration} {scenario} published {file} after rejection.");
        if (File.Exists(sentinel))
            throw new InvalidOperationException($"{configuration} {scenario} executed the fixture module initializer during inspection.");
        Console.WriteLine($"POST_MVVM_SDK_DISCOVERY_REJECTED|{configuration}|{expectedCode}|{scenario}");
    }
    finally
    {
        Environment.SetEnvironmentVariable("RUNIC_POST_MVVM_SENTINEL", null);
        if (File.Exists(sentinel)) File.Delete(sentinel);
    }
}

ArtifactSet VerifyConfiguration(string configuration)
{
    string sentinel = Artifact(configuration, "module-initializer-sentinel.txt");
    Environment.SetEnvironmentVariable("RUNIC_POST_MVVM_SENTINEL", sentinel);
    try
    {
        CleanFixture(serialOwner, configuration);
        if (File.Exists(Artifact(configuration, "stages.log"))) throw new InvalidOperationException($"Clean retained generated {configuration} discovery artifacts.");
        if (File.Exists(sentinel)) File.Delete(sentinel);

        string firstBuild = RunDotnet("build", fixture, "-c", configuration, "--nologo", "--no-restore");
        Equal(1, Count(firstBuild, "POST_MVVM_SDK_DISCOVERY_TARGET"), "The outer build did not invoke exactly one discovery target.");
        ArtifactSet first = VerifyArtifacts(configuration, sentinel);

        string rebuild = RunDotnet("build", fixture, "-c", configuration, "--nologo", "--no-restore");
        Equal(1, Count(rebuild, "POST_MVVM_SDK_DISCOVERY_TARGET"), "A rebuild did not invoke exactly one discovery target.");
        ArtifactSet second = VerifyArtifacts(configuration, sentinel);
        if (first != second)
            throw new InvalidOperationException($"{configuration} post-MVVM emitted bytes changed on a rebuild.");
        return second;
    }
    finally
    {
        Environment.SetEnvironmentVariable("RUNIC_POST_MVVM_SENTINEL", null);
        if (File.Exists(sentinel)) File.Delete(sentinel);
    }
}

ArtifactSet VerifyArtifacts(string configuration, string sentinel)
{
    string stages = Artifact(configuration, "stages.log");
    string[] stageLines = File.ReadAllLines(stages).Where(line => !String.IsNullOrWhiteSpace(line)).ToArray();
    if (!stageLines.SequenceEqual(["bootstrap", "outer"], StringComparer.Ordinal))
        throw new InvalidOperationException($"Unexpected {configuration} discovery stages: {String.Join(',', stageLines)}.");

    string irPath = Artifact(configuration, "view-bridge.ir.json");
    string esmPath = Artifact(configuration, "view-bridge.contract.ts");
    string adapterPath = Artifact(configuration, "PostMvvmDiscoveryAdapter.g.cs");
    string readyPath = Artifact(configuration, "view-bridge.ready.json");
    string ir = File.ReadAllText(irPath);
    using JsonDocument document = JsonDocument.Parse(ir);
    JsonElement rootElement = document.RootElement;
    string fingerprint = rootElement.GetProperty("fingerprint").GetString() ?? throw new InvalidOperationException("IR has no fingerprint.");
    if (rootElement.GetProperty("format").GetString() != "runic-sdk.post-mvvm-discovery-ir")
        throw new InvalidOperationException("The post-MVVM discovery IR format changed.");
    JsonElement model = rootElement.GetProperty("models").EnumerateArray().Single();
    if (!model.GetProperty("fields").EnumerateArray().Any(field => field.GetProperty("name").GetString() == "title" && field.GetProperty("codec").GetString() == "string"))
        throw new InvalidOperationException("The generated CommunityToolkit Title property was not discovered.");
    string[] kinds = model.GetProperty("commands").EnumerateArray().Select(command => command.GetProperty("kind").GetString() ?? String.Empty).ToArray();
    if (!kinds.Contains("toolkit-async-relay", StringComparer.Ordinal) || !kinds.Contains("reactive-command-string-string", StringComparer.Ordinal))
        throw new InvalidOperationException("The compiled command shapes were not discovered.");
    JsonElement[] views = rootElement.GetProperty("views").EnumerateArray().ToArray();
    if (views.Length != 2 || !views.Any(view => view.GetProperty("kind").GetString() == "window") || !views.Any(view => view.GetProperty("kind").GetString() == "view"))
        throw new InvalidOperationException("The explicit Window/View candidates were not discovered.");
    VerifyFixtureRuntime(rootElement, model.GetProperty("name").GetString() ?? throw new InvalidOperationException("The fixture model has no name."));

    using JsonDocument ready = JsonDocument.Parse(File.ReadAllText(readyPath));
    if (ready.RootElement.GetProperty("fingerprint").GetString() != fingerprint ||
        ready.RootElement.GetProperty("ir").GetString() != Path.GetFileName(irPath) ||
        ready.RootElement.GetProperty("esm").GetString() != Path.GetFileName(esmPath) ||
        ready.RootElement.GetProperty("adapter").GetString() != Path.GetFileName(adapterPath))
        throw new InvalidOperationException("The ready manifest does not identify the deterministic IR, ESM, and adapter.");
    string esm = File.ReadAllText(esmPath);
    if (!esm.Contains($"export const fingerprint = \"{fingerprint}\";", StringComparison.Ordinal) ||
        !esm.Contains("readonly title: string;", StringComparison.Ordinal) ||
        !esm.Contains("\"name\":\"saveCommand\",\"kind\":\"toolkit-async-relay\"", StringComparison.Ordinal) ||
        !esm.Contains("\"name\":\"refreshCommand\",\"kind\":\"reactive-command-string-string\"", StringComparison.Ordinal) ||
        !esm.Contains("readonly saveCommand: () => Promise<void>;", StringComparison.Ordinal) ||
        !esm.Contains("readonly refreshCommand: (value: string) => Promise<string>;", StringComparison.Ordinal) ||
        !esm.Contains("export const runtime =", StringComparison.Ordinal) ||
        !esm.Contains("\"id\":\"titleRead\"", StringComparison.Ordinal) ||
        !esm.Contains("\"suffix\":\".title.read\"", StringComparison.Ordinal) ||
        !esm.Contains("\"id\":\"titleSnapshot\"", StringComparison.Ordinal) ||
        !esm.Contains("\"suffix\":\".title.snapshot\"", StringComparison.Ordinal) ||
        !esm.Contains("\"id\":\"titleWrite\"", StringComparison.Ordinal) ||
        !esm.Contains("\"suffix\":\".title.write\"", StringComparison.Ordinal) ||
        !esm.Contains("\"id\":\"titleWriteChecked\"", StringComparison.Ordinal) ||
        !esm.Contains("\"suffix\":\".title.writeChecked\"", StringComparison.Ordinal) ||
        !esm.Contains("export type TitleWriteReceipt", StringComparison.Ordinal) ||
        !esm.Contains("generation: string", StringComparison.Ordinal) ||
        !esm.Contains("\"id\":\"saveStart\"", StringComparison.Ordinal) ||
        !esm.Contains("\"id\":\"saveStatus\"", StringComparison.Ordinal) ||
        !esm.Contains("\"id\":\"saveWait\"", StringComparison.Ordinal) ||
        !esm.Contains("\"member\":\"saveCommand\"", StringComparison.Ordinal) ||
        !esm.Contains("\"id\":\"refresh\"", StringComparison.Ordinal) ||
        !esm.Contains("\"member\":\"refreshCommand\"", StringComparison.Ordinal) ||
        !esm.Contains("export function encodeRouteRequest", StringComparison.Ordinal) ||
        !esm.Contains("export function decodeRouteReply", StringComparison.Ordinal) ||
        !esm.Contains("unexpected fields", StringComparison.Ordinal) ||
        esm.Contains("from \"effect", StringComparison.OrdinalIgnoreCase) ||
        esm.Contains("from 'effect", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The plain TypeScript contract lost generated MVVM metadata, types, or its no-Effect boundary.");
    string adapterText = File.ReadAllText(adapterPath);
    if (!adapterText.Contains($"public const string Fingerprint = \"{fingerprint}\";", StringComparison.Ordinal) ||
        !adapterText.Contains("saveCommand:toolkit-async-relay", StringComparison.Ordinal) ||
        !adapterText.Contains("refreshCommand:reactive-command-string-string", StringComparison.Ordinal) ||
        !adapterText.Contains("model.Title", StringComparison.Ordinal) ||
        !adapterText.Contains("model.SaveCommand.ExecuteAsync(null)", StringComparison.Ordinal) ||
        !adapterText.Contains("Signal.ToTask(model.RefreshCommand.Execute(value)", StringComparison.Ordinal) ||
        !adapterText.Contains("AttachFixtureRoutes", StringComparison.Ordinal) ||
        !adapterText.Contains("ObserveFixtureTitle", StringComparison.Ordinal) ||
        !adapterText.Contains(".title.read", StringComparison.Ordinal) ||
        !adapterText.Contains(".title.snapshot", StringComparison.Ordinal) ||
        !adapterText.Contains(".title.write", StringComparison.Ordinal) ||
        !adapterText.Contains(".title.writeChecked", StringComparison.Ordinal) ||
        !adapterText.Contains("FixtureCheckedTitleField", StringComparison.Ordinal) ||
        !adapterText.Contains(".SetDirect(value)", StringComparison.Ordinal) ||
        !adapterText.Contains("TryReadTitleCheckedWrite", StringComparison.Ordinal) ||
        !adapterText.Contains("FixtureTitleReceiptPayload", StringComparison.Ordinal) ||
        !adapterText.Contains("route + \".save.start\"", StringComparison.Ordinal) ||
        !adapterText.Contains("WindowSaveStatusRoute = \"postmvvm.save.status\"", StringComparison.Ordinal) ||
        !adapterText.Contains("WindowSaveWaitRoute = \"postmvvm.save.wait\"", StringComparison.Ordinal) ||
        !adapterText.Contains("BindWindowOperationRoutes", StringComparison.Ordinal) ||
        !adapterText.Contains("route + \".refresh\"", StringComparison.Ordinal) ||
        !adapterText.Contains("StartOperationFromPresentation", StringComparison.Ordinal) ||
        !adapterText.Contains("WaitForTerminalFromPresentationAsync", StringComparison.Ordinal) ||
        !adapterText.Contains("LookupOperationFromPresentation", StringComparison.Ordinal) ||
        !adapterText.Contains("TryReadSaveStart", StringComparison.Ordinal) ||
        !adapterText.Contains("TryReadWindowSaveObservation", StringComparison.Ordinal) ||
        !adapterText.Contains("FixtureSaveAdmissionPayload", StringComparison.Ordinal) ||
        !adapterText.Contains("TryReadRefresh", StringComparison.Ordinal) ||
        !adapterText.Contains("FixtureRefreshPayload", StringComparison.Ordinal) ||
        !adapterText.Contains("documentEpoch", StringComparison.Ordinal) ||
        !adapterText.Contains("presentationId", StringComparison.Ordinal) ||
        !adapterText.Contains("isPresentationMounted", StringComparison.Ordinal) ||
        !adapterText.Contains("publishTitleInvalidation", StringComparison.Ordinal) ||
        adapterText.Contains("transport.Publish", StringComparison.Ordinal) ||
        adapterText.Contains("FixtureTitlePayload(ReadTitle(model), includeOk: false)", StringComparison.Ordinal))
        throw new InvalidOperationException("The C# adapter stub disagrees with the metadata contract.");
    if (File.Exists(sentinel)) throw new InvalidOperationException("Metadata inspection executed the fixture module initializer.");
    VerifyOuterAdapter(configuration);
    return new ArtifactSet(ir, esm, adapterText, File.ReadAllText(readyPath));
}

void VerifyFixtureRuntime(JsonElement rootElement, string modelName)
{
    JsonElement runtime = rootElement.GetProperty("runtime");
    if (runtime.GetProperty("format").GetString() != "runic-sdk.post-mvvm-fixture-runtime" ||
        runtime.GetProperty("formatVersion").GetInt32() != 3 ||
        runtime.GetProperty("referencePrefix").GetString() != "mounted-reference-prefix")
        throw new InvalidOperationException("The fixture runtime mapping is not versioned or does not declare its unresolved reference prefix boundary.");

    JsonElement[] routes = runtime.GetProperty("routes").EnumerateArray().ToArray();
    if (routes.Length != 8 || routes.Any(route => route.GetProperty("model").GetString() != modelName))
        throw new InvalidOperationException("The fixture runtime mapping did not retain the one compiled model's complete route set.");
    VerifyRoute("titleRead", "title", "field-read", ".title.read", ["documentEpoch", "presentationId"], ["ok", "title"]);
    VerifyRoute("titleSnapshot", "title", "field-snapshot", ".title.snapshot", ["documentEpoch", "presentationId"], ["ok", "current"]);
    VerifyRoute("titleWrite", "title", "field-write", ".title.write", ["documentEpoch", "presentationId", "title"], ["ok", "title"]);
    VerifyCheckedTitleRoute();
    VerifyOperationRoute("saveStart", "toolkit-async-relay-admission", ".save.start",
        ["documentEpoch", "presentationId", "requestId"], ["accepted", "requestId", "kind", "state", "error"]);
    VerifyOperationRoute("saveStatus", "toolkit-async-relay-status", "postmvvm.save.status",
        ["referenceId", "documentEpoch", "requestId"], ["requestId", "kind", "state", "error"], "window");
    VerifyOperationRoute("saveWait", "toolkit-async-relay-wait", "postmvvm.save.wait",
        ["referenceId", "documentEpoch", "requestId"], ["requestId", "kind", "state", "error"], "window");
    VerifyRoute("refresh", "refreshCommand", "reactive-command-string-string", ".refresh", ["documentEpoch", "presentationId", "value"], ["ok", "value"]);

    void VerifyRoute(string id, string member, string kind, string suffix, string[] requestFields, string[] acceptedFields)
    {
        JsonElement route = routes.Single(candidate => candidate.GetProperty("id").GetString() == id);
        if (route.GetProperty("member").GetString() != member || route.GetProperty("kind").GetString() != kind || route.GetProperty("suffix").GetString() != suffix
            || route.GetProperty("scope").GetString() != "model")
            throw new InvalidOperationException($"The fixture runtime mapping changed {id}'s member, kind, or relative route shape.");
        string[] request = route.GetProperty("request").GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("name").GetString() ?? String.Empty).ToArray();
        if (!request.SequenceEqual(requestFields, StringComparer.Ordinal))
            throw new InvalidOperationException($"The fixture runtime mapping changed {id}'s exact request wire shape.");
        JsonElement[] variants = route.GetProperty("reply").GetProperty("variants").EnumerateArray().ToArray();
        JsonElement accepted = variants.Single(variant => variant.GetProperty("ok").GetBoolean());
        JsonElement rejected = variants.Single(variant => !variant.GetProperty("ok").GetBoolean());
        string[] acceptedWire = accepted.GetProperty("wire").GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("name").GetString() ?? String.Empty).ToArray();
        string[] rejectedWire = rejected.GetProperty("wire").GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("name").GetString() ?? String.Empty).ToArray();
        if (!acceptedWire.SequenceEqual(acceptedFields, StringComparer.Ordinal) || !rejectedWire.SequenceEqual(["ok"], StringComparer.Ordinal))
            throw new InvalidOperationException($"The fixture runtime mapping changed {id}'s strict reply wire shape.");
    }

    void VerifyOperationRoute(string id, string kind, string suffix, string[] requestFields, string[] replyFields, string scope = "model")
    {
        JsonElement route = routes.Single(candidate => candidate.GetProperty("id").GetString() == id);
        if (route.GetProperty("member").GetString() != "saveCommand" || route.GetProperty("kind").GetString() != kind
            || route.GetProperty("suffix").GetString() != suffix || route.GetProperty("scope").GetString() != scope)
            throw new InvalidOperationException($"The fixture runtime mapping changed {id}'s Save operation route.");
        string[] request = route.GetProperty("request").GetProperty("fields").EnumerateArray()
            .Select(field => field.GetProperty("name").GetString() ?? String.Empty).ToArray();
        JsonElement[] variants = route.GetProperty("reply").GetProperty("variants").EnumerateArray().ToArray();
        string[] reply = variants.Single().GetProperty("wire").GetProperty("fields").EnumerateArray()
            .Select(field => field.GetProperty("name").GetString() ?? String.Empty).ToArray();
        if (!request.SequenceEqual(requestFields, StringComparer.Ordinal)
            || !reply.SequenceEqual(replyFields, StringComparer.Ordinal)
            || variants[0].GetProperty("ok").ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException($"The fixture runtime mapping changed {id}'s exact Save wire shape.");
    }

    void VerifyCheckedTitleRoute()
    {
        JsonElement route = routes.Single(candidate => candidate.GetProperty("id").GetString() == "titleWriteChecked");
        if (route.GetProperty("member").GetString() != "title" || route.GetProperty("kind").GetString() != "field-write-checked"
            || route.GetProperty("suffix").GetString() != ".title.writeChecked" || route.GetProperty("scope").GetString() != "model")
            throw new InvalidOperationException("The fixture checked Title route changed its member or model-relative shape.");
        string[] request = route.GetProperty("request").GetProperty("fields").EnumerateArray()
            .Select(field => field.GetProperty("name").GetString() ?? String.Empty).ToArray();
        if (!request.SequenceEqual(["documentEpoch", "presentationId", "requestId", "title", "expected"], StringComparer.Ordinal))
            throw new InvalidOperationException("The fixture checked Title route changed its exact request wire shape.");
        JsonElement[] variants = route.GetProperty("reply").GetProperty("variants").EnumerateArray().ToArray();
        if (variants.Length != 5 || variants[0].GetProperty("name").GetString() != "applied" || !variants[0].GetProperty("ok").GetBoolean()
            || variants[1].GetProperty("name").GetString() != "conflict" || variants[1].GetProperty("ok").GetBoolean()
            || variants[2].GetProperty("name").GetString() != "expired" || variants[2].GetProperty("ok").GetBoolean()
            || variants[3].GetProperty("name").GetString() != "committed-with-error" || variants[3].GetProperty("ok").GetBoolean()
            || variants[4].GetProperty("name").GetString() != "rejected" || variants[4].GetProperty("ok").GetBoolean()
            || variants.Any(variant => !variant.GetProperty("wire").GetProperty("fields").EnumerateArray()
                .Select(field => field.GetProperty("name").GetString() ?? String.Empty)
                .SequenceEqual(["ok", "kind", "current", "error"], StringComparer.Ordinal)))
            throw new InvalidOperationException("The fixture checked Title route changed its receipt wire shape.");
    }
}

void VerifyOuterAdapter(string configuration)
{
    string assemblyPath = Path.Combine(OwnerRoot(serialOwner), "outer", "bin", configuration, "net10.0", "Runic.Application.Bridge.PostMvvmDiscovery.Fixture.dll");
    using var context = new MetadataLoadContext(new PathAssemblyResolver(MetadataClosure(assemblyPath)));
    Assembly assembly = context.LoadFromAssemblyPath(assemblyPath);
    Type adapter = assembly.GetType("Runic.Application.Bridge.PostMvvmFixture.Generated.PostMvvmDiscoveryAdapter", throwOnError: true)!;
    string? stage = adapter.GetField("Stage", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() as string;
    if (stage != "post-mvvm-outer") throw new InvalidOperationException("The generated adapter constant was not compiled by the outer build.");
    if (adapter.GetField("Commands", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() is not string commands ||
        !commands.Contains("saveCommand:toolkit-async-relay", StringComparison.Ordinal) ||
        !commands.Contains("refreshCommand:reactive-command-string-string", StringComparison.Ordinal))
        throw new InvalidOperationException("The compiled adapter omitted generated command metadata.");
}

void VerifyConcurrentSameKeyBuildOwnership()
{
    const string key = "same-key-concurrent";
    const string singleOwner = "11111111111111111111111111111111";
    const string multipleOwner = "22222222222222222222222222222222";
    string barrier = Path.Combine(Path.GetTempPath(), "runic-post-mvvm-barrier-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(barrier);
    try
    {
        RestoreFixture(singleOwner, key);
        RestoreFixture(multipleOwner, key);
        using Process single = StartFixtureBuild(singleOwner, key, "POST_MVVM_INTERFACE_SINGLE_MAPPING", barrier);
        using Process multiple = StartFixtureBuild(multipleOwner, key, "POST_MVVM_INTERFACE_MULTIPLE_MODELS", barrier);
        ProcessResult singleResult = WaitForProcess(single, "single-model");
        ProcessResult multipleResult = WaitForProcess(multiple, "multiple-model");
        if (singleResult.ExitCode != 0 || multipleResult.ExitCode != 0)
            throw new InvalidOperationException($"Concurrent same-key discovery builds failed. Single:\n{singleResult.Output}\nMultiple:\n{multipleResult.Output}");
        Equal(1, Count(singleResult.Output, "POST_MVVM_SDK_DISCOVERY_TARGET"), "The single-model concurrent build did not invoke one discovery target.");
        Equal(1, Count(multipleResult.Output, "POST_MVVM_SDK_DISCOVERY_TARGET"), "The multiple-model concurrent build did not invoke one discovery target.");
        VerifyConcurrentBundle(singleOwner, key, expectedBindings: 1);
        VerifyConcurrentBundle(multipleOwner, key, expectedBindings: 2);
        Console.WriteLine("POST_MVVM_SDK_CONCURRENT_OWNER_OK|same-selection-key|one-and-two-bindings|isolated-obj-bin-bootstrap-inspector-generated");
    }
    finally
    {
        if (Directory.Exists(barrier)) Directory.Delete(barrier, recursive: true);
    }
}

void VerifyConcurrentBundle(string owner, string key, int expectedBindings)
{
    string ownerRoot = OwnerRoot(owner, key);
    string generated = Path.Combine(ownerRoot, "generated", "Debug", "net10.0");
    string ir = File.ReadAllText(Path.Combine(generated, "view-bridge.ir.json"));
    string esm = File.ReadAllText(Path.Combine(generated, "view-bridge.contract.ts"));
    string adapter = File.ReadAllText(Path.Combine(generated, "PostMvvmDiscoveryAdapter.g.cs"));
    using JsonDocument irDocument = JsonDocument.Parse(ir);
    string fingerprint = irDocument.RootElement.GetProperty("fingerprint").GetString() ?? throw new InvalidOperationException("Concurrent IR has no fingerprint.");
    if (irDocument.RootElement.GetProperty("bindings").GetArrayLength() != expectedBindings)
        throw new InvalidOperationException($"Concurrent owner {owner} did not retain its expected {expectedBindings} bindings.");
    using JsonDocument ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(generated, "view-bridge.ready.json")));
    if (ready.RootElement.GetProperty("fingerprint").GetString() != fingerprint ||
        !esm.Contains($"export const fingerprint = \"{fingerprint}\";", StringComparison.Ordinal) ||
        !adapter.Contains($"public const string Fingerprint = \"{fingerprint}\";", StringComparison.Ordinal))
        throw new InvalidOperationException($"Concurrent owner {owner} published a mixed ready bundle.");
    if (!File.Exists(Path.Combine(ownerRoot, "bootstrap", "bin", "Debug", "net10.0", "Runic.Application.Bridge.PostMvvmDiscovery.Fixture.dll")) ||
        !File.Exists(Path.Combine(ownerRoot, "inspector", "bin", "Debug", "net10.0", "Runic.Application.Bridge.Inspector.dll")) ||
        !File.Exists(Path.Combine(ownerRoot, "outer", "bin", "Debug", "net10.0", "Runic.Application.Bridge.PostMvvmDiscovery.Fixture.dll")))
        throw new InvalidOperationException($"Concurrent owner {owner} did not retain owner-scoped bootstrap, inspector, and outer outputs.");
    if (Directory.EnumerateFiles(ownerRoot, "*.tmp", SearchOption.AllDirectories).Any())
        throw new InvalidOperationException($"Concurrent owner {owner} retained a temporary publication file.");
    if (Directory.EnumerateFiles(ownerRoot, "view-bridge.ready.json", SearchOption.AllDirectories).Count() != 1)
        throw new InvalidOperationException($"Concurrent owner {owner} did not retain exactly one ready bundle.");
    string fixtureOutput = Path.Combine(root, "tests", "fixtures", "application", "PostMvvmDiscovery", "obj", "runic-post-mvvm-discovery");
    if (Directory.Exists(Path.Combine(fixtureOutput, key, "Debug")) || Directory.Exists(Path.Combine(fixtureOutput, "bootstrap", key)))
        throw new InvalidOperationException("Concurrent same-key builds wrote a legacy shared generated or bootstrap directory.");
}

string OwnerRoot(string owner, string key = outputKey) => Path.Combine(root, "tests", "fixtures", "application", "PostMvvmDiscovery", "obj", "runic-post-mvvm-discovery", key, "owners", owner);

string Artifact(string configuration, string file) => Path.Combine(OwnerRoot(serialOwner), "generated", configuration, "net10.0", file);

IEnumerable<string> MetadataClosure(string assemblyPath)
{
    string directory = Path.GetDirectoryName(assemblyPath) ?? throw new InvalidOperationException("The compiled fixture assembly has no directory.");
    string? trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
    if (String.IsNullOrWhiteSpace(trusted)) throw new InvalidOperationException("The runtime did not expose its trusted platform assembly closure.");
    return Directory.EnumerateFiles(directory, "*.dll")
        .Concat(trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        .Append(assemblyPath)
        .Distinct(StringComparer.OrdinalIgnoreCase);
}

string RunDotnet(params string[] arguments)
{
    ProcessResult result = ExecuteDotnet(arguments);
    if (result.ExitCode != 0) throw new InvalidOperationException($"dotnet {String.Join(' ', arguments)} failed:\n{result.Output}");
    return result.Output;
}

ProcessResult ExecuteDotnet(params string[] arguments)
{
    return ExecuteDotnetWithOwner(serialOwner, arguments);
}

void RestoreFixture(string owner, string key = outputKey)
{
    ProcessResult result = ExecuteDotnetWithOwner(owner, ["restore", fixture, "--nologo", "-p:RunicPostMvvmDiscoveryOutputKey=" + key]);
    if (result.ExitCode != 0) throw new InvalidOperationException($"Could not restore fixture owner {owner}:\n{result.Output}");
}

void CleanFixture(string owner, string configuration)
{
    ProcessResult result = ExecuteDotnetWithOwner(owner, ["clean", fixture, "-c", configuration, "--nologo"]);
    if (result.ExitCode != 0) throw new InvalidOperationException($"Could not clean fixture owner {owner}:\n{result.Output}");
    RestoreFixture(owner);
}

ProcessResult ExecuteDotnetWithOwner(string owner, params string[] arguments)
{
    using Process process = StartDotnet(arguments, owner);
    string output = process.StandardOutput.ReadToEnd();
    string error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return new ProcessResult(process.ExitCode, output + error);
}

ProcessResult ExecuteRawDotnet(params string[] arguments)
{
    using Process process = StartDotnet(arguments, owner: null);
    string output = process.StandardOutput.ReadToEnd();
    string error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return new ProcessResult(process.ExitCode, output + error);
}

ProcessResult ExecuteFixtureWrapper(string buildArgument)
{
    ProcessStartInfo start;
    if (OperatingSystem.IsWindows())
    {
        string script = Path.Combine(root, "eng", "build", "run-post-mvvm-discovery.ps1");
        start = new ProcessStartInfo("pwsh") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("& '" + script.Replace("'", "''", StringComparison.Ordinal) + "' -BuildArguments @('" +
            buildArgument.Replace("'", "''", StringComparison.Ordinal) + "'); exit $LASTEXITCODE");
    }
    else
    {
        string script = Path.Combine(root, "eng", "build", "run-post-mvvm-discovery.sh");
        start = new ProcessStartInfo("bash") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(buildArgument);
    }
    using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the post-MVVM owner wrapper.");
    string output = process.StandardOutput.ReadToEnd();
    string error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return new ProcessResult(process.ExitCode, output + error);
}

Process StartFixtureBuild(string owner, string key, string scenario, string barrier)
{
    return StartDotnet(
        ["build", fixture, "-c", "Debug", "--nologo", "--no-restore", "-p:RunicPostMvvmDiscoveryOutputKey=" + key, "-p:DefineConstants=" + scenario],
        owner,
        new Dictionary<string, string> {
            ["RUNIC_POST_MVVM_BARRIER_DIRECTORY"] = barrier,
            ["RUNIC_POST_MVVM_BARRIER_PARTICIPANT"] = owner,
        });
}

ProcessResult WaitForProcess(Process process, string label)
{
    Task<string> output = process.StandardOutput.ReadToEndAsync();
    Task<string> error = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(120_000))
    {
        process.Kill(entireProcessTree: true);
        throw new TimeoutException($"The {label} same-key fixture build did not finish within 120 seconds.");
    }
    return new ProcessResult(process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
}

Process StartDotnet(IEnumerable<string> arguments, string? owner, IReadOnlyDictionary<string, string>? environment = null)
{
    var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    string operation = arguments.First();
    if (operation is "restore" or "build" or "clean")
    {
        start.ArgumentList.Add("-m:1");
        start.ArgumentList.Add("/nr:false");
    }
    if (owner is not null)
    {
        start.ArgumentList.Add("-p:RunicPostMvvmDiscoveryBuildOwner=" + owner);
        start.ArgumentList.Add("-p:RunicPostMvvmDiscoveryOwnerDriver=true");
    }
    if (environment is not null)
        foreach ((string key, string value) in environment) start.Environment[key] = value;
    return Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
}

string FindRoot()
{
    for (DirectoryInfo? current = new(Environment.CurrentDirectory); current is not null; current = current.Parent)
        if (File.Exists(Path.Combine(current.FullName, "RunicSdk.slnx"))) return current.FullName;
    throw new InvalidOperationException("Could not locate the SDK root.");
}

int Count(string value, string marker)
{
    int count = 0;
    for (int index = 0; (index = value.IndexOf(marker, index, StringComparison.Ordinal)) >= 0; index += marker.Length) count++;
    return count;
}

void Equal<T>(T expected, T actual, string message) where T : IEquatable<T>
{
    if (!expected.Equals(actual)) throw new InvalidOperationException(message + $" Expected {expected}; actual {actual}.");
}

internal sealed record ProcessResult(int ExitCode, string Output);
internal sealed record ArtifactSet(string Ir, string Esm, string Adapter, string Ready);
