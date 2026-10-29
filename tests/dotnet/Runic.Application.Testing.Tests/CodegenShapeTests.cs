using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Application.Testing;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Exercises the generated codecs for CodegenShapeViewModel through its routes.
internal sealed class CodegenShapeTests
{
    [Test]
    public void GeneratedCodecsRoundTripModelShapes()
    {
        var model = new CodegenShapeViewModel();
        using var host = new RunicWindowTestHost<CodegenShapeViewModel>(model,
            (transport, content, vm) => new CodegenShapeBridge(transport, vm, content: content), new RunicWindowTestHostOptions { RootRoute = "codegenShape", ViewLocator = new TestViewLocator() });

        using (var snapshot = host.RawSnapshot())
        {
            var state = snapshot.RootElement.GetProperty("state");
            Require(state.GetProperty("mode").GetString() == "None",
                "An enum alias did not write the first declared name for its value.");
            Require(state.GetProperty("numbers").GetArrayLength() == 2 && state.GetProperty("optionalNames").ValueKind == JsonValueKind.Null,
                "ImmutableArray state was not written.");
            Require(state.GetProperty("origin").ValueKind == JsonValueKind.Null
                && state.GetProperty("corner").GetProperty("y").GetInt32() == 4,
                "Nullable or non-nullable struct DTO state was not written.");
            Require(state.GetProperty("labels")[1].ValueKind == JsonValueKind.Null,
                "A nullable array element was not written as null.");
            Require(state.GetProperty("stamp").GetString() == "2026-10-03T12:30:00+02:00",
                "A DateTimeOffset snapshot lost its offset.");
            // A default JsonElement holds no value and crosses as null.
            Require(state.GetProperty("any").ValueKind == JsonValueKind.Null && state.GetProperty("maybeAny").ValueKind == JsonValueKind.Null
                && state.GetProperty("node").GetRawText() == """{"a":[1,true,null,"s"]}"""
                && state.GetProperty("json").GetRawText() == """{"any":[1],"node":null,"required":"required"}""",
                $"JSON values were not written as they are: {state}");
        }

        // Any JSON value reads into a JsonElement or a JsonNode (#304).
        Set(host, "Any", """{"x":[1,2.50,"y"],"n":null}""");
        Require(model.Any.GetProperty("x")[1].GetRawText() == "2.50" && model.Any.GetProperty("n").ValueKind == JsonValueKind.Null,
            "A JsonElement did not keep the JSON it was set to.");
        Set(host, "Any", "null");
        Require(model.Any.ValueKind == JsonValueKind.Null, "A JsonElement did not accept JSON null.");
        Set(host, "MaybeAny", "\"text\"");
        Require(model.MaybeAny is { ValueKind: JsonValueKind.String } text && text.GetString() == "text", "A JsonElement? was not read.");
        Set(host, "MaybeAny", "null");
        Require(model.MaybeAny is null, "A JsonElement? did not read JSON null as null.");
        Set(host, "Node", "[1,{\"a\":false}]");
        Require(model.Node is JsonArray { Count: 2 } array && array[1]!["a"]!.GetValue<bool>() == false, "A JsonNode? was not read.");
        Set(host, "Node", "null");
        Require(model.Node is null, "A JsonNode? did not read JSON null as null.");
        Set(host, "Json", """{"any":null,"node":{"b":2},"required":0}""");
        Require(model.Json is { Any.ValueKind: JsonValueKind.Null, Node: JsonObject, Required: JsonValue } && model.Json.Required.GetValue<int>() == 0,
            "A DTO with JSON members did not round-trip.");
        Rejected(host, "Json", """{"any":null,"node":null,"required":null}""");
        Require(model.Json.Required.GetValue<int>() == 0, "A non-nullable JsonNode accepted JSON null.");
        using (var snapshot = host.RawSnapshot())
            Require(snapshot.RootElement.GetProperty("state").GetProperty("json").GetRawText() == """{"any":null,"node":{"b":2},"required":0}""",
                "A JSON member read from the page was not written back unchanged.");

        // Values produced by new Date().toISOString() and by hand in a browser.
        Set(host, "Stamp", "\"2026-10-03T12:34:56.789Z\"");
        Require(model.Stamp == new DateTimeOffset(2026, 10, 3, 12, 34, 56, 789, TimeSpan.Zero),
            "A browser ISO DateTimeOffset was not accepted.");
        Set(host, "When", "\"2026-10-03T12:34:56.789Z\"");
        Require(model.When == new DateTime(2026, 10, 3, 12, 34, 56, 789, DateTimeKind.Utc) && model.When.Kind is DateTimeKind.Utc,
            "A browser ISO DateTime was not accepted as UTC.");
        Set(host, "At", "\"08:15:00\"");
        Require(model.At == new TimeOnly(8, 15), "An ISO time without fractional seconds was not accepted.");

        Set(host, "Mode", "\"Default\"");
        Require(model.Mode == CodegenShapeMode.Default, "An enum alias name could not be read.");
        Set(host, "Mode", "\"Active\"");
        Require(model.Mode == CodegenShapeMode.Active, "An enum name could not be read.");
        Set(host, "Numbers", "[5,6,7]");
        Require(model.Numbers is [5, 6, 7], "ImmutableArray<int> did not round-trip through its codec.");
        Set(host, "OptionalNames", "[\"a\",null]");
        Require(model.OptionalNames is { } names && names is ["a", null], "ImmutableArray<string?>? did not round-trip.");
        Set(host, "Origin", "{\"x\":1,\"y\":2}");
        Require(model.Origin == new CodegenPoint(1, 2), "A nullable struct DTO could not be read.");
        Set(host, "Origin", "null");
        Require(model.Origin is null, "A nullable struct DTO did not accept null.");
        Set(host, "Tags", "[\"x\",\"y\"]");
        Require(model.Tags is ["x", "y"], "ReadOnlyCollection<string> did not round-trip.");
        Set(host, "Labels", "[null,\"z\"]");
        Require(model.Labels is [null, "z"], "A nullable array element was rejected.");
        Set(host, "Editable", "[\"one\"]");
        model.Editable.Add("two");
        Require(model.Editable.Count == 2, "An IList<T> codec produced a fixed-size collection.");

        // A closed generic DTO reads T with the nullability of its use site,
        // in every position: a member, a list, an array, a dictionary value
        // and a nested generic DTO.
        Set(host, "Required", Slot("\"v\"", "\"i\"", "\"a\"", "\"m\""));
        Require(model.Required is { Value: "v", Fallback: null, Items: ["i"], Values: ["a"], Inner.Value: "v" }
            && model.Required.Map["key"] == "m", "CodegenSlot<string> did not round-trip.");
        foreach (var json in new[]
        {
            Slot("null", "\"i\"", "\"a\"", "\"m\""), Slot("\"v\"", "null", "\"a\"", "\"m\""),
            Slot("\"v\"", "\"i\"", "null", "\"m\""), Slot("\"v\"", "\"i\"", "\"a\"", "null"),
            Slot("\"v\"", "\"i\"", "\"a\"", "\"m\"").Replace("\"inner\":{\"value\":\"v\"}", "\"inner\":{\"value\":null}", StringComparison.Ordinal),
        })
            Rejected(host, "Required", json);
        Require(model.Required is { Value: "v", Items: ["i"], Values: ["a"], Inner.Value: "v" } && model.Required.Map["key"] == "m",
            "A rejected CodegenSlot<string> write changed the model.");
        Set(host, "Optional", Slot("null", "null", "null", "null"));
        Require(model.Optional is { Value: null, Fallback: null, Items: [null], Values: [null], Inner.Value: null }
            && model.Optional.Map["key"] is null, "CodegenSlot<string?> did not accept null.");

        // A member inherited from a generic base class follows the derived
        // type's argument.
        Set(host, "Derived", "{\"inherited\":\"b\",\"own\":\"o\"}");
        Require(model.Derived is { Inherited: "b", Own: "o" }, "CodegenDerived<string> did not round-trip.");
        Rejected(host, "Derived", "{\"inherited\":null,\"own\":\"o\"}");
        Require(model.Derived.Inherited == "b", "A rejected CodegenDerived<string> write changed the model.");

        // A plain command input from typeof() is non-nullable throughout.
        using (var applied = JsonDocument.Parse(host.Transport.Command("codegenShape", "apply", Slot("\"c\"", "\"i\"", "\"a\"", "\"m\""))))
            Require(applied.RootElement.GetProperty("ok").GetBoolean() && model.Required.Value == "c",
                $"The CodegenSlot<string> command input was rejected: {applied.RootElement}");
        using (var rejected = JsonDocument.Parse(host.Transport.Command("codegenShape", "apply", Slot("null", "\"i\"", "\"a\"", "\"m\""))))
            Require(!rejected.RootElement.GetProperty("ok").GetBoolean() && model.Required.Value == "c",
                $"The CodegenSlot<string> command input accepted null: {rejected.RootElement}");

        static string Slot(string value, string item, string element, string entry) =>
            $"{{\"value\":{value},\"fallback\":null,\"items\":[{item}],\"values\":[{element}],\"map\":{{\"key\":{entry}}},\"inner\":{{\"value\":{value}}}}}";
    }

    [Test]
    public void NullableReactiveTypeArgumentsReachTheClient()
    {
        var client = File.ReadAllText(Path.Combine(GeneratedTypeScriptDirectory(), "nullableReactive.ts"));
        foreach (var expected in new[]
        {
            "echo(input: string | null): Promise<NullableReactiveState>;",
            "export interface NullableReactiveEchoOperation extends BridgeOperation<string | null> {}",
            "const echoOperation: BridgeOperationCodec<string | null> = ",
            "recoverEcho(requestId: string) { return view.recover(\"echo\", requestId, echoOperation); },",
            "handle(handler: (input: string, context: NullableReactiveInteractionContext) => string | null | Promise<string | null>): () => void;",
        })
            Require(client.Contains(expected, StringComparison.Ordinal),
                $"The generated client lost a nullable ReactiveUI type argument: missing '{expected}'.");

        using var model = new NullableReactiveViewModel();
        using var host = new RunicWindowTestHost<NullableReactiveViewModel>(model,
            (transport, content, vm) => new NullableReactiveBridge(transport, vm, content: content), new RunicWindowTestHostOptions { RootRoute = "nullableReactive", ViewLocator = new TestViewLocator() });
        using (var snapshot = host.RawSnapshot())
            Require(snapshot.RootElement.GetProperty("state").TryGetProperty("isEchoExecuting", out var executing)
                && executing.ValueKind == JsonValueKind.False,
                "A ReactiveUI command with an argument did not write the isEchoExecuting field its client declares.");
    }

    [Test]
    public async Task NullableReactiveOperationsAcceptNullAsync()
    {
        using var model = new NullableReactiveViewModel();
        using var host = new RunicWindowTestHost<NullableReactiveViewModel>(model,
            (transport, content, vm) => new NullableReactiveBridge(transport, vm, content: content), new RunicWindowTestHostOptions { RootRoute = "nullableReactive", ViewLocator = new TestViewLocator() });
        var operation = await host.Root.StartAsync(vm => vm.EchoCommand, "null", "echo-null");
        Require(operation.Admission is RunicOperationAdmissionKind.Accepted or RunicOperationAdmissionKind.Duplicate,
            $"A null ReactiveUI command input was rejected: {operation.AdmissionJson}");
        var completion = await operation.WaitAsync();
        Require(completion.Kind == RunicOperationStatusKind.Succeeded && completion.Result?.ValueKind == JsonValueKind.Null,
            $"A null ReactiveUI command result was not encoded: {completion.Json}");
    }

    // Generated TypeScript reads like hand-written code: named types after the
    // C# types, literal unions for enums, XML comments, and real parameter names.
    // #648: a get-only NavigationSelection is a state slot whose wire value is SelectedIndex, or null for -1.
    [Test]
    public void NavigationSelectionSlotShape()
    {
        var parts = Runic.Application.Views.Generated.BridgeContractShape.Parts(typeof(NavTabsViewModel));
        Require(parts.Contains("selection:Runic.Application.Testing.Tests.NavTabsViewModel:tabs")
                && !parts.Any(part => part.Contains("NavTabsViewModel.tabs", StringComparison.Ordinal)),
            $"The contract fingerprint describes the selection slot by more than its part:\n{string.Join('\n', parts)}");
        var directory = GeneratedTypeScriptDirectory();
        var tabs = File.ReadAllText(Path.Combine(directory, "navTabs.ts")).ReplaceLineEndings("\n");
        foreach (var expected in new[]
        {
            "  /** The shown tab. */\n  readonly tabs: number | null;",
            "  tabs: (wire: unknown) => (wire === null ? null : bridgeWire.integer(wire, -2147483648, 2147483647)),",
            "  selectTab(index: number): Promise<NavTabsState>;",
        })
            Require(tabs.Contains(expected, StringComparison.Ordinal), $"navTabs.ts is missing:\n{expected}\n---\n{tabs}");
        Require(!tabs.Contains("setTabs", StringComparison.Ordinal), "A get-only selection slot has a setter.");
        var mock = File.ReadAllText(Path.Combine(directory, "navTabs.mock.ts")).ReplaceLineEndings("\n");
        Require(mock.Contains("const typed = value as number | null;", StringComparison.Ordinal),
            $"The selection slot's mock field is not a nullable number:\n{mock}");
    }

    [Test]
    public void GeneratedTypeScriptSurface()
    {
        var directory = GeneratedTypeScriptDirectory();
        var types = File.ReadAllText(Path.Combine(directory, "types.ts")).ReplaceLineEndings("\n");
        var documented = File.ReadAllText(Path.Combine(directory, "documented.ts")).ReplaceLineEndings("\n");
        var collection = File.ReadAllText(Path.Combine(directory, "collectionDelta.ts")).ReplaceLineEndings("\n");
        var shape = File.ReadAllText(Path.Combine(directory, "codegenShape.ts")).ReplaceLineEndings("\n");
        foreach (var (file, text, expected) in new[]
        {
            ("types.ts", types, "export type CodegenShapeMode = \"None\" | \"Default\" | \"Active\";"),
            ("types.ts", types, "/**\n * How the documented item is shown.\n *\n * - `List`: One item per row.\n * - `Grid`: Items in a grid.\n */\nexport type DocumentedLayout = \"List\" | \"Grid\";"),
            ("types.ts", types, "/** A documented item. */\nexport interface DocumentedItem {\n  /** The item's `title`. */\n  readonly title: string;\n}"),
            ("types.ts", types, "export interface CodegenPoint {\n  readonly x: number;\n  readonly y: number;\n}"),
            // Uses of one generic DTO with different member nullability get
            // their own declarations.
            ("types.ts", types, "export interface CodegenSlotOfString {\n  readonly value: string;\n  readonly fallback: string | null;\n  readonly items: readonly string[];\n  readonly values: readonly string[];\n  readonly map: Readonly<Record<string, string>>;\n  readonly inner: CodegenInnerOfString;\n}"),
            ("types.ts", types, "export interface CodegenSlotOfNullableOfString {\n  readonly value: string | null;\n  readonly fallback: string | null;\n  readonly items: readonly (string | null)[];\n  readonly values: readonly (string | null)[];\n  readonly map: Readonly<Record<string, string | null>>;\n  readonly inner: CodegenInnerOfNullableOfString;\n}"),
            ("types.ts", types, "export interface CodegenInnerOfString {\n  readonly value: string;\n}"),
            ("types.ts", types, "export interface CodegenInnerOfNullableOfString {\n  readonly value: string | null;\n}"),
            ("types.ts", types, "export interface CodegenDerivedOfString {\n  readonly inherited: string;\n  readonly own: string;\n}"),
            ("codegenShape.ts", shape, "  readonly required: CodegenSlotOfString;\n  readonly optional: CodegenSlotOfNullableOfString;\n  readonly derived: CodegenDerivedOfString;"),
            // Any JSON value is unknown; a JsonNode is never JSON null.
            ("codegenShape.ts", shape, "  readonly any: unknown;\n  readonly maybeAny: unknown;\n  readonly node: unknown;\n  readonly json: CodegenJson;"),
            ("types.ts", types, "export interface CodegenJson {\n  readonly any?: unknown;\n  readonly node: unknown;\n  readonly required: NonNullable<unknown>;\n}"),
            ("codegenShape.ts", shape, "any: (wire: unknown) => bridgeWire.json(wire),"),
            ("codegenShape.ts", shape, "required: bridgeWire.json(value[\"required\"], false)"),
            // The typeof() command input shares the state property's declaration.
            ("codegenShape.ts", shape, "  apply(codegenSlot: CodegenSlotOfString): Promise<CodegenShapeState>;"),
            ("codegenShape.ts", shape, "required: (wire: unknown) => bridgeWire.object<CodegenSlotOfString>(wire, value => ({ value: bridgeWire.string(value[\"value\"]), "),
            ("types.ts", types, "export type DataShapePayload =\n  | ({ readonly $case: \"count\" } & DataShapeCount)\n  | ({ readonly $case: \"text\" } & DataShapeText);"),
            ("documented.ts", documented, "import type { CodegenPoint, DocumentedItem, DocumentedLayout } from \"./types.js\";\nexport type { CodegenPoint, DocumentedItem, DocumentedLayout } from \"./types.js\";"),
            ("documented.ts", documented, "  /** The current layout. */\n  readonly layout: DocumentedLayout;"),
            ("documented.ts", documented, "  /** The note being edited. */\n  readonly note: string;"),
            ("documented.ts", documented, "  /** Switches to `layout`. */\n  changeLayout(layout: DocumentedLayout): Promise<DocumentedState>;"),
            // C# accessibility is the contract: a public setter, and only a
            // public setter, becomes a set… member that names it.
            ("documented.ts", documented, "  /**\n   * The note being edited.\n   *\n   * Calls the public C# setter of `DocumentedViewModel.Note`.\n   */\n  setNote(value: string): Promise<DocumentedState>;"),
            ("documented.ts", documented, "  readonly item: DocumentedItem;\n  /** Whether the note has `unsaved` changes. */\n  readonly isDirty: boolean;"),
            // C# `long` is a TypeScript bigint, and the generated comment says so.
            ("dataShape.ts", File.ReadAllText(Path.Combine(directory, "dataShape.ts")).ReplaceLineEndings("\n"),
                "  /** A C# `long` is a `bigint`: a JavaScript `number` is exact only up to 2^53. */\n  readonly \"exact-id\": bigint;"),
            // The connect timeout is an option of the generated connect function (default 5000 ms).
            ("documented.ts", documented, "export function connectDocumented(options?: BridgeConnectOptions): Promise<DocumentedClient & globalThis.Disposable> {"),
            ("documented.ts", documented, "members, checkedFields: checkedFieldNames, texts: [\"note\"], timeout: options?.timeout, signal: options?.signal });"),
            ("documented.ts", documented, " * Generated TypeScript names its enum and DTO types after the C# types,"),
            ("documented.ts", documented, "layout: (wire: unknown) => bridgeWire.enumName<DocumentedLayout>(wire, [\"List\", \"Grid\"]),"),
            ("collectionDelta.ts", collection, "rows: defineCollection<CollectionRow>(wire => bridgeWire.object<CollectionRow>(wire,"),
        })
            Require(text.Contains(expected, StringComparison.Ordinal),
                $"Generated {file} is missing:\n{expected}\n--- {file} ---\n{text}");
        var generatedReactive = File.ReadAllText(Path.Combine(directory, "generatedReactive.ts")).ReplaceLineEndings("\n");
        Require(!documented.Contains("setIsDirty", StringComparison.Ordinal) && !documented.Contains("setLayout", StringComparison.Ordinal),
            "A Toolkit property with a private setter became settable from the web View.");
        // The summaries on [Reactive] and [ObservableProperty] partial properties are on their
        // defining parts, which the XML documentation file omits (runic-sdk #169).
        Require(generatedReactive.Contains("  /**\n   * The name to save.\n   *\n   * Calls the public C# setter of `GeneratedReactiveViewModel.Name`.\n   */\n  setName(value: string)", StringComparison.Ordinal)
            && generatedReactive.Contains("  /** Whether the name changed since the last save. */\n  readonly isDirty: boolean;", StringComparison.Ordinal)
            && !generatedReactive.Contains("setIsDirty", StringComparison.Ordinal),
            $"A ReactiveUI [Reactive] property's setter accessibility did not decide its set… member:\n{generatedReactive}");
        Require(!documented.Contains("instance wrapping", StringComparison.Ordinal),
            "A Toolkit command copied its generated property comment instead of its method documentation.");
        Require(Count(types, "export interface CodegenPoint ") == 1,
            "A C# type used by several ViewModels was declared more than once.");
        Require(Count(types, "export interface CodegenSlotOf") == 2 && !types.Contains("CodegenSlotOfString2", StringComparison.Ordinal),
            "A typeof() command input and a state property using CodegenSlot<string> did not share one declaration.");
        foreach (var path in Directory.GetFiles(directory, "*.ts"))
        {
            var text = File.ReadAllText(path);
            Require(!text.Contains("@deprecated", StringComparison.Ordinal) && !text.Contains("argument:", StringComparison.Ordinal),
                $"{Path.GetFileName(path)} still has a deprecated alias or an anonymous 'argument' parameter.");
        }

        // A client imports only the protocol runtimes its ViewModel uses, from
        // the generated entry, and the decoders as a namespace so bundlers can
        // drop the ones it does not call.
        var reactive = File.ReadAllText(Path.Combine(directory, "nullableReactive.ts")).ReplaceLineEndings("\n");
        foreach (var (file, text, expected) in new[]
        {
            ("documented.ts", documented, "import { connectView, disposable, viewReferences, type BridgeMemberDecoders } from \"@runic-artifex/views/generated\";\nimport * as bridgeWire from \"@runic-artifex/views/generated/wire\";\n"),
            ("nullableReactive.ts", reactive, "import { connectView, disposable, viewReferences, type BridgeMemberDecoders, type BridgeOperationCodec, defineInteractions, bridgeOperations } from \"@runic-artifex/views/generated\";\n"),
            ("nullableReactive.ts", reactive, "const interactionDefinitions = defineInteractions({"),
            ("nullableReactive.ts", reactive, "interactions: interactionDefinitions, handlers: options?.handlers, operations: bridgeOperations, timeout: options?.timeout, signal: options?.signal });"),
            ("collectionDelta.ts", collection, "import { connectView, disposable, viewReferences, type BridgeMemberDecoders, defineCollection, defineCollections } from \"@runic-artifex/views/generated\";\n"),
            ("collectionDelta.ts", collection, "const collectionDefinitions = defineCollections({"),
        })
            Require(text.Contains(expected, StringComparison.Ordinal),
                $"Generated {file} is missing:\n{expected}\n--- {file} ---\n{text}");
        foreach (var unused in new[] { "defineInteractions", "bridgeOperations", "defineCollection", "interactions:", "operations:", "collections:" })
            Require(!documented.Contains(unused, StringComparison.Ordinal),
                $"documented.ts has no interactions, operations or keyed collections but references '{unused}'.");
        foreach (var path in Directory.GetFiles(directory, "*.ts"))
        {
            var text = File.ReadAllText(path).ReplaceLineEndings("\n");
            var rootImport = text.Split('\n').FirstOrDefault(line => line.EndsWith(" from \"@runic-artifex/views\";", StringComparison.Ordinal)) ?? "";
            Require(!rootImport.Contains("connectView", StringComparison.Ordinal) && !rootImport.Contains("bridgeWire", StringComparison.Ordinal),
                $"{Path.GetFileName(path)} imports generated-code runtime from the application entry: {rootImport}");
            Require(!text.Contains("bridgeWire.", StringComparison.Ordinal)
                || text.Contains("import * as bridgeWire from \"@runic-artifex/views/generated/wire\";", StringComparison.Ordinal),
                $"{Path.GetFileName(path)} uses bridgeWire without the namespace import from @runic-artifex/views/generated/wire.");
        }

        static int Count(string text, string value)
        {
            var count = 0;
            for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
                 index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal)) count++;
            return count;
        }
    }

    // Names that collide, generic DTOs over arrays, case-less enums, wire
    // names that are special on JavaScript objects, reserved parameter names
    // and a comment terminator inside documentation.
    [Test]
    public void GeneratedNamesAvoidCollisionsAndReservedWords()
    {
        var directory = GeneratedTypeScriptDirectory();
        var types = File.ReadAllText(Path.Combine(directory, "types.ts")).ReplaceLineEndings("\n");
        var naming = File.ReadAllText(Path.Combine(directory, "naming.ts")).ReplaceLineEndings("\n");
        foreach (var (file, text, expected) in new[]
        {
            ("types.ts", types, "export interface AlphaTag {\n  readonly name: string;\n}"),
            ("types.ts", types, "export interface BetaTag {\n  readonly label: string;\n}"),
            ("types.ts", types, "export interface TestsNamingState {\n  readonly count: number;\n}"),
            ("types.ts", types, "export interface GenericBoxOfArrayOfInt32 {\n  readonly value: readonly number[] | null;\n}"),
            ("types.ts", types, "export type EmptyKind = never;"),
            ("types.ts", types, "export interface Testsdelete {\n  readonly id: number;\n}"),
            ("naming.ts", naming, "  readonly removed: Testsdelete;"),
            ("naming.ts", naming, "  readonly first: AlphaTag;\n  readonly second: BetaTag;\n  readonly inner: TestsNamingState;\n  readonly box: GenericBoxOfArrayOfInt32;\n  readonly nothing: EmptyKind | null;"),
            ("naming.ts", naming, "This comment contains *\\/ and must not end the TSDoc block."),
            ("naming.ts", naming, "  [\"__proto__\"]: (wire: unknown) => bridgeWire.string(wire),"),
            ("naming.ts", naming, "  constructor: (wire: unknown) => bridgeWire.string(wire),"),
            ("naming.ts", naming, "  prototype: (wire: unknown) => bridgeWire.string(wire),"),
            ("naming.ts", naming, "  rename(defaultValue: string): Promise<NamingState>;"),
            ("naming.ts", naming, "  show(viewValue: string): Promise<NamingState>;"),
            ("naming.ts", naming, "  readonly new: string;"),
            ("naming.ts", naming, "  newItem(): Promise<NamingState>;"),
        })
            Require(text.Contains(expected, StringComparison.Ordinal),
                $"Generated {file} is missing:\n{expected}\n--- {file} ---\n{text}");
        Require(!types.Contains("export interface Tag ", StringComparison.Ordinal),
            "Same-named C# types were not qualified with their namespaces.");
        Require(!types.Contains("export interface delete ", StringComparison.Ordinal),
            "A C# type named like a TypeScript reserved word was not renamed.");
    }

    // The generated client reads a real NamingViewModel state over Views protocol 4.
    [Test]
    public async Task GeneratedClientReadsSpecialWireNamesAsync()
    {
        var directory = GeneratedTypeScriptDirectory();
        using var host = new RunicWindowTestHost<NamingViewModel>(new NamingViewModel(),
            (transport, content, vm) => new NamingBridge(transport, vm, content: content), new RunicWindowTestHostOptions { RootRoute = "naming", ViewLocator = new TestViewLocator() });
        var root = Path.GetFullPath(Path.Combine(directory, "..", "..", "..", "..", "..", ".."));
        using var live = new GeneratedClientLiveHost().Serve("naming", host.Transport);
        await GeneratedHarnessProcess.RunAsync("bun", [Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests",
            "GeneratedSpecialNamesHarness.ts"), directory], root, live).ConfigureAwait(false);
    }

    // A generated module's own bindings (runtime imports, the wire type, page
    // reference caches) must not collide with names derived from user types
    // (#258). ReservedNamesFixture declares a ViewModel or type for each.
    [Test]
    public async Task GeneratedModulesKeepTheirOwnBindingsApartFromUserNamesAsync()
    {
        var directory = GeneratedTypeScriptDirectory();
        var root = Path.GetFullPath(Path.Combine(directory, "..", "..", "..", "..", "..", ".."));
        string[] modules = ["wire", "view", "bridge", "bridgeValidation", "define", "typedView", "shelf", "shelfReferences"];
        await GeneratedHarnessProcess.RunAsync("bun", [Path.Combine(root, "node_modules", "typescript", "bin", "tsc"),
            "--noEmit", "--strict", "--noPropertyAccessFromIndexSignature", "--noImplicitReturns",
            "--noFallthroughCasesInSwitch", "--target", "ES2022", "--module", "ESNext",
            "--moduleResolution", "bundler", "--skipLibCheck",
            .. modules.SelectMany(module => new[] { Path.Combine(directory, module + ".ts"), Path.Combine(directory, module + ".mock.ts") })],
            root).ConfigureAwait(false);

        string Read(string file) => File.ReadAllText(Path.Combine(directory, file)).ReplaceLineEndings("\n");
        foreach (var (file, expected) in new[]
        {
            ("wire.ts", "export interface WireState {"),
            ("wire.ts", "const members: BridgeMemberDecoders<WireState> = {"),
            ("view.ts", "import type { BridgeConnectOptions, ViewClient as _ViewClient } from \"@runic-artifex/views\";"),
            ("view.ts", "import { connectView as _connectView, disposable, viewReferences, type BridgeMemberDecoders } from \"@runic-artifex/views/generated\";"),
            ("view.ts", "export interface ViewClient extends _ViewClient<ViewState> {"),
            ("view.ts", "export function connectView(options?: BridgeConnectOptions): Promise<ViewClient & globalThis.Disposable> {"),
            ("view.ts", "  const view = await _connectView<ViewState>({ contract: bridgeContract, "),
            ("bridge.ts", "export type BridgeInteractionContext = _BridgeInteractionContext;"),
            ("bridge.ts", "export interface BridgeStreamOperation extends _BridgeStreamOperation<number> {}"),
            ("bridge.ts", "export interface BridgeSaveOperation extends BridgeOperation<number> {}"),
            ("bridgeValidation.ts", "  readonly validation: _BridgeValidationState;"),
            ("define.ts", "const interactionDefinitions = _defineInteractions({"),
            ("define.ts", "export interface defineInteractions {"),
            ("typedView.mock.ts", "import { mockTypedView as _mockTypedView, "),
            ("typedView.mock.ts", "  return _mockTypedView(bridge, spec, definition) as TypedViewMock;"),
            ("shelf.ts", "import { pageShelfReferences, type ShelfReferencesPageReference } from \"./shelfReferences.js\";"),
            ("shelf.ts", "const _pageShelfReferences = viewReferences<ShelfPageReference>("),
            // A module without a colliding user name keeps the plain bindings.
            ("naming.ts", "const members: BridgeMemberDecoders<NamingState> = {"),
            ("naming.ts", "  const view = await connectView<NamingState>({ contract: bridgeContract, "),
        })
            Require(Read(file).Contains(expected, StringComparison.Ordinal),
                $"Generated {file} is missing:\n{expected}\n--- {file} ---\n{Read(file)}");
        var types = Read("types.ts");
        Require(!types.Contains("export interface bridgeWire ", StringComparison.Ordinal)
                && types.Contains("export interface ReservedNamesbridgeWire {", StringComparison.Ordinal),
            $"The named type bridgeWire was not renamed away from the decoder namespace import:\n{types}");
    }

    private static string GeneratedTypeScriptDirectory()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx")))
                return Path.Combine(directory.FullName, "tests", "dotnet", "Runic.Application.Testing.Tests", "obj", "bridge-frontend", "generated");
        throw new InvalidOperationException("Could not locate the Runic SDK workspace root.");
    }

    private static void Set(RunicWindowTestHost<CodegenShapeViewModel> host, string property, string json)
    {
        using var reply = JsonDocument.Parse(host.Transport.Set("codegenShape", Member(property), json));
        Require(reply.RootElement.GetProperty("ok").GetBoolean(), $"Setting {property} to {json} was rejected: {reply.RootElement}");
    }

    private static void Rejected(RunicWindowTestHost<CodegenShapeViewModel> host, string property, string json)
    {
        using var reply = JsonDocument.Parse(host.Transport.Set("codegenShape", Member(property), json));
        Require(!reply.RootElement.GetProperty("ok").GetBoolean(), $"Setting {property} to {json} was accepted: {reply.RootElement}");
    }

    // The property's wire name: Required -> required.
    private static string Member(string property) => char.ToLowerInvariant(property[0]) + property[1..];

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
