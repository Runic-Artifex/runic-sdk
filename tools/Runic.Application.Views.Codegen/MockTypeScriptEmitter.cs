using System.Text;
using System.Text.Json;

// The typed test double of one generated client: `{name}.mock.ts` exports
// `mock{Name}(bridge, definition)`. Its state, handler and failure types derive
// from the client module, so renaming a ViewModel member breaks the mock at
// compile time. The codecs here mirror the client's wire encoding; the
// scheduling, routes and protocol live in @runic-artifex/views/mock.
internal sealed record MockValueField(string WireName, BridgeTypeGraph Graph);
internal sealed record MockContentField(string WireName, string ReferenceType, bool IsCollection, bool IsNullable);
internal sealed record MockSetter(string Method, string Route, string WireName, string ValueType, string ReadExpression, string? CheckedRoute);
internal sealed record MockCommand(string Method, string Route, string? InputType, string? ReadExpression, string? AvailabilityField);
internal sealed record MockOperation(string Method, string Member, string InputType, string ResultType, string? DecodeInput,
    string? EncodeResult, bool IsStream);
internal sealed record MockInteraction(string Name, string InputType, string OutputType, string EncodeInput, string DecodeOutput);
internal sealed record MockCollection(string WireName, string ItemType, string Encode, string Decode, string Key);

internal sealed record MockTypeScriptPlan(
    string ShortName,
    string Prefix,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<MockValueField> ValueFields,
    IReadOnlyList<MockContentField> ContentFields,
    IReadOnlyList<(string Field, string Default)> Defaults,
    IReadOnlyList<string> CheckedFields,
    IReadOnlyList<MockSetter> Setters,
    IReadOnlyList<MockCommand> Commands,
    IReadOnlyList<MockOperation> Operations,
    IReadOnlyList<MockInteraction> Interactions,
    IReadOnlyList<MockCollection> Collections,
    string Contract);

internal static class MockTypeScriptEmitter
{
    internal static string Emit(MockTypeScriptPlan plan, string clientModule)
    {
        var name = plan.ShortName;
        var state = $"{name}MockState";
        var body = new StringBuilder();

        var optional = plan.Defaults.Select(entry => Quote(entry.Field)).ToArray();
        var content = plan.ContentFields.Select(field => Quote(field.WireName)).ToArray();
        var omitted = optional.Concat(content).ToArray();
        var kinds = string.Join(" | ", plan.Kinds.Select(Quote));
        body.AppendLine($"/** The state of a mock {name}: the client state, with content as `{{ kind, id }}` and command state optional. */");
        body.Append($"export type {state} = ");
        body.Append(omitted.Length == 0 ? $"{name}State" : $"Omit<{name}State, {string.Join(" | ", omitted)}>");
        if (optional.Length > 0) body.Append($" & Partial<Pick<{name}State, {string.Join(" | ", optional)}>>");
        if (plan.ContentFields.Count > 0)
        {
            body.AppendLine(" & {");
            foreach (var field in plan.ContentFields)
                body.AppendLine($"  readonly {Key(field.WireName)}: {(field.IsCollection ? $"readonly ({field.ReferenceType})[]" : field.ReferenceType)}{(field.IsNullable ? " | null" : "")};");
            body.Append('}');
        }
        body.AppendLine(";");
        body.AppendLine();

        body.AppendLine($"export interface {name}MockDefinition {{");
        body.AppendLine($"  /** Presents the mock as content on route `content{{id}}`; omit it for the root route `{plan.Prefix}`. */");
        body.AppendLine("  readonly id?: string;");
        if (plan.Kinds.Count > 1) body.AppendLine($"  readonly kind?: {kinds};");
        body.AppendLine($"  readonly state: {state};");
        if (plan.Setters.Count > 0)
        {
            body.AppendLine("  /** Run before a setter or checked write applies its value; return more changes or throw to reject it. */");
            body.AppendLine("  readonly setters?: {");
            foreach (var setter in plan.Setters)
                body.AppendLine($"    readonly {setter.Method}?: MockSetterHandler<{state}, {setter.ValueType}>;");
            body.AppendLine("  };");
        }
        if (plan.Commands.Count > 0)
        {
            body.AppendLine("  readonly commands?: {");
            foreach (var command in plan.Commands)
                body.AppendLine($"    readonly {command.Method}?: MockCommandHandler<{state}{(command.InputType is null ? "" : $", [input: {command.InputType}]")}>;");
            body.AppendLine("  };");
        }
        var queries = plan.Commands.Where(command => command.InputType is not null).ToArray();
        if (queries.Length > 0)
        {
            body.AppendLine("  /** Availability of commands with an argument. */");
            body.AppendLine("  readonly canExecute?: {");
            foreach (var command in queries)
                body.AppendLine($"    readonly {command.Method}?: (state: {state}, input: {command.InputType}) => boolean;");
            body.AppendLine("  };");
        }
        if (plan.Operations.Count > 0)
        {
            body.AppendLine("  /** Operation handlers; without one an operation runs its command handler and succeeds. */");
            body.AppendLine("  readonly operations?: {");
            foreach (var operation in plan.Operations)
                body.AppendLine($"    readonly {operation.Method}?: MockTypedOperationHandler<{state}, {operation.InputType}, {operation.ResultType}> | \"manual\";");
            body.AppendLine("  };");
        }
        body.AppendLine("}");
        body.AppendLine();

        var members = new List<string>();
        if (plan.Collections.Count > 0)
            members.Add($"  readonly collections: {{ {string.Join(" ", plan.Collections.Select(collection => $"readonly {Key(collection.WireName)}: MockTypedCollection<{collection.ItemType}>;"))} }};");
        if (plan.Operations.Count > 0)
            members.Add($"  readonly operations: {{ {string.Join(" ", plan.Operations.Select(operation => $"readonly {operation.Method}: readonly MockTypedOperation<{operation.InputType}, {operation.ResultType}>[];"))} }};");
        if (plan.Interactions.Count > 0)
            members.Add($"  readonly interactions: {{ {string.Join(" ", plan.Interactions.Select(interaction => $"readonly {interaction.Name}: MockTypedInteraction<{interaction.InputType}, {interaction.OutputType}>;"))} }};");
        var baseType = $"MockTypedView<{state}, {name}Client, {kinds}>";
        if (members.Count == 0) body.AppendLine($"export interface {name}Mock extends {baseType} {{}}");
        else
        {
            body.AppendLine($"export interface {name}Mock extends {baseType} {{");
            foreach (var member in members) body.AppendLine(member);
            body.AppendLine("}");
        }
        body.AppendLine();

        body.AppendLine("const spec: MockTypedViewSpec = {");
        body.AppendLine($"  kind: {Quote(plan.Kinds[0])},");
        body.AppendLine($"  route: {Quote(plan.Prefix)},");
        body.AppendLine($"  contract: {Quote(plan.Contract)},");
        if (plan.ValueFields.Count == 0) body.AppendLine("  fields: {},");
        else
        {
            body.AppendLine("  fields: {");
            foreach (var field in plan.ValueFields)
                body.AppendLine($"    {Key(field.WireName)}: {{ encode: (value: unknown) => {Encode(field.Graph.EncodeTypeScript("typed"), field.Graph.TypeScriptType())}, decode: (wire: unknown) => {BridgeTypeGraph.ArrowBody(field.Graph.EmitTypeScriptDecoder("wire"))} }},");
            body.AppendLine("  },");
        }
        body.AppendLine(plan.Defaults.Count == 0 ? "  defaults: {}," : $"  defaults: {{ {string.Join(", ", plan.Defaults.Select(entry => $"{Key(entry.Field)}: {entry.Default}"))} }},");
        if (plan.CheckedFields.Count > 0)
            body.AppendLine($"  checkedFields: [{string.Join(", ", plan.CheckedFields.Select(Quote))}],");
        if (plan.Collections.Count > 0)
        {
            body.AppendLine("  collections: {");
            foreach (var collection in plan.Collections)
                body.AppendLine($"    {Key(collection.WireName)}: {{ encode: (value: unknown) => {Encode(collection.Encode, collection.ItemType)}, decode: (wire: unknown) => {BridgeTypeGraph.ArrowBody(collection.Decode)}, key: (item: {collection.ItemType}) => {collection.Key} }},");
            body.AppendLine("  },");
        }
        if (plan.Setters.Count > 0)
        {
            body.AppendLine("  setters: {");
            foreach (var setter in plan.Setters)
                body.AppendLine($"    {setter.Method}: {{ route: {Quote(setter.Route)}, field: {Quote(setter.WireName)}, read: (raw: unknown) => {setter.ReadExpression}{(setter.CheckedRoute is null ? "" : $", checked: {Quote(setter.CheckedRoute)}")} }},");
            body.AppendLine("  },");
        }
        if (plan.Commands.Count > 0)
        {
            body.AppendLine("  commands: {");
            foreach (var command in plan.Commands)
                body.AppendLine($"    {command.Method}: {{ route: {Quote(command.Route)}{(command.ReadExpression is null ? "" : $", read: (raw: unknown) => {{ const wire: unknown = JSON.parse(raw as string); return {command.ReadExpression}; }}")}{(command.AvailabilityField is null ? "" : $", available: {Quote(command.AvailabilityField)}")} }},");
            body.AppendLine("  },");
        }
        if (plan.Operations.Count > 0)
        {
            body.AppendLine("  operations: {");
            foreach (var operation in plan.Operations)
                body.AppendLine($"    {operation.Method}: {{ member: {Quote(operation.Member)}{(operation.DecodeInput is null ? "" : $", decodeInput: (wire: unknown) => {BridgeTypeGraph.ArrowBody(operation.DecodeInput)}")}{(operation.EncodeResult is null ? "" : $", encodeResult: (value: unknown) => {Encode(operation.EncodeResult, operation.ResultType)}")}{(operation.IsStream ? ", stream: true" : "")} }},");
            body.AppendLine("  },");
        }
        if (plan.Interactions.Count > 0)
        {
            body.AppendLine("  interactions: {");
            foreach (var interaction in plan.Interactions)
                body.AppendLine($"    {interaction.Name}: {{ encodeInput: (value: unknown) => {Encode(interaction.EncodeInput, interaction.InputType)}, decodeOutput: (wire: unknown) => {BridgeTypeGraph.ArrowBody(interaction.DecodeOutput)} }},");
            body.AppendLine("  },");
        }
        body.AppendLine("};");
        body.AppendLine();
        body.AppendLine($"/**");
        body.AppendLine($" * Serves the {name} routes from `bridge` with typed state and handlers, so a test");
        body.AppendLine($" * can connect the generated client without .NET.");
        body.AppendLine($" */");
        body.AppendLine($"export function mock{name}(bridge: MockBridge, definition: {name}MockDefinition): {name}Mock {{");
        body.AppendLine($"  return mockTypedView(bridge, spec, definition) as {name}Mock;");
        body.AppendLine("}");

        var text = body.ToString();
        var mockImports = new List<string> { "mockTypedView", "type MockBridge", "type MockTypedView", "type MockTypedViewSpec" };
        if (plan.Setters.Count > 0) mockImports.Add("type MockSetterHandler");
        if (plan.Commands.Count > 0) mockImports.Add("type MockCommandHandler");
        if (plan.Operations.Count > 0) mockImports.AddRange(["type MockTypedOperation", "type MockTypedOperationHandler"]);
        if (plan.Collections.Count > 0) mockImports.Add("type MockTypedCollection");
        if (plan.Interactions.Count > 0) mockImports.Add("type MockTypedInteraction");
        if (plan.ContentFields.Count > 0) mockImports.Add("type MockReference");
        var module = new StringBuilder();
        module.AppendLine(GeneratedOutput.Header);
        if (text.Contains("bridgeWire.", StringComparison.Ordinal))
            module.AppendLine("import { bridgeWire } from \"@runic-artifex/views\";");
        module.AppendLine($"import {{ {string.Join(", ", mockImports)} }} from \"@runic-artifex/views/mock\";");
        module.AppendLine($"import type {{ {name}Client, {name}State }} from \"./{clientModule}.js\";");
        module.AppendLine(TypeScriptModules.NamedTypeImports);
        module.AppendLine();
        module.Append(text);
        return module.ToString();
    }

    // Encoders read a typed value; the spec receives it as unknown.
    private static string Encode(string expression, string type) =>
        $"{{ const typed = value as {type}; return {expression}; }}";

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static string Key(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] is '_' or '$')
            && name.All(character => char.IsLetterOrDigit(character) || character is '_' or '$') && name != "__proto__"
            ? name : $"[{Quote(name)}]";
}
