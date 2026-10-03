using System.Reflection;
using System.Text;
using Runic.Application.Views.Codegen.ReactiveUI;

/// <summary>
/// Emits the optional browser-facing half of a ReactiveUI interaction.  Keeping
/// this separate from Program makes the interaction protocol reviewable: the
/// main generator merely decides where the generated fragments belong.
/// </summary>
internal static class InteractionCodeEmitter
{
    internal static GeneratedInteractionPlan[] Discover(IEnumerable<PropertyInfo> members, string modelName,
        string contractFingerprint)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractFingerprint);

        var plans = new List<GeneratedInteractionPlan>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var nullability = new NullabilityInfoContext();
        foreach (var property in members)
        {
            var interaction = ReactiveInteractionInspector.InspectContract(property);
            if (interaction is null) continue;
            if (property.GetMethod is null)
                throw new NotSupportedException($"{modelName}.{property.Name}: an interaction needs a public getter.");
            if (property.SetMethod?.IsPublic == true)
                throw new NotSupportedException($"{modelName}.{property.Name}: interactions are application-owned and cannot have a public setter.");

            var publicName = LowerFirst(property.Name);
            if (!names.Add(publicName))
                throw new NotSupportedException($"{modelName}.{property.Name}: duplicate generated interaction name '{publicName}'.");
            plans.Add(new GeneratedInteractionPlan(property, interaction,
                BridgeTypeGraph.Discover(interaction.Input,
                    ContractNullability.Argument(property, nullability, [interaction.Input, interaction.Output], 0),
                    $"{modelName}.{property.Name}.input"),
                BridgeTypeGraph.Discover(interaction.Output,
                    ContractNullability.Argument(property, nullability, [interaction.Input, interaction.Output], 1),
                    $"{modelName}.{property.Name}.output"),
                $"{contractFingerprint}:interaction:{property.Name}"));
        }
        return [.. plans];
    }

    /// <summary>
    /// Checks every generated TypeScript state/view member and every .NET
    /// route suffix before emitting any files. A collision is otherwise a
    /// surprisingly easy way to ship a client whose declaration looks valid
    /// but whose object literal overwrites a method, whose JSON snapshot has
    /// duplicate keys, or whose route binding fails only at runtime.
    /// </summary>
    internal static void ValidatePublicSurface(string modelName, IEnumerable<PropertyInfo> properties,
        IEnumerable<PropertyInfo> commands, IReadOnlyDictionary<PropertyInfo, GeneratedCommandPlan> commandPlans,
        IEnumerable<GeneratedInteractionPlan> interactions, IEnumerable<PropertyInfo> checkedProperties,
        Func<PropertyInfo, string> wireName, bool hasErrors, bool hasValidation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(commandPlans);
        ArgumentNullException.ThrowIfNull(interactions);
        ArgumentNullException.ThrowIfNull(checkedProperties);
        ArgumentNullException.ThrowIfNull(wireName);

        var stateProperties = properties.ToArray();
        var commandProperties = commands.ToArray();
        var checkedMembers = checkedProperties.ToArray();
        var state = new MemberNames(modelName, "state");
        state.Add("revision", "the generated revision field");
        if (hasValidation) state.Add("validation", "the generated validation state");
        if (checkedMembers.Length > 0) state.Add("__runicFields", "the generated checked-field metadata");
        foreach (var property in stateProperties)
        {
            state.Add(wireName(property), property.Name);
            if (hasErrors) state.Add(wireName(property) + "Errors", property.Name + " errors");
        }
        foreach (var command in commandProperties)
        {
            var plan = commandPlans[command];
            var name = command.Name[..^"Command".Length];
            if (!plan.HasArgument) state.Add("can" + name, command.Name + " availability");
            if (plan.HasExecutionState) state.Add("is" + name + "Executing", command.Name + " execution state");
        }

        var view = new MemberNames(modelName, "view");
        view.Add("snapshot", "the generated snapshot property");
        view.Add("subscribe", "the generated subscribe method");
        view.Add("dispose", "the generated dispose method");
        // A view with a then() method is thenable: `await connectX()` would
        // call it instead of resolving to the view.
        view.Add("then", "Promise resolution (a then() member makes the connected view thenable)");
        var interactionPlans = interactions.ToArray();
        if (interactionPlans.Length > 0) view.Add("interactions", "the generated interactions property");
        foreach (var property in stateProperties.Where(property => property.SetMethod?.IsPublic == true))
            view.Add("set" + property.Name, property.Name + " setter");
        foreach (var property in checkedMembers) view.Add("write" + property.Name, property.Name + " checked writer");
        if (checkedMembers.Length > 0) view.Add("fieldBaseline", "the generated checked-field baseline method");
        foreach (var command in commandProperties)
        {
            var plan = commandPlans[command];
            var name = command.Name[..^"Command".Length];
            view.Add(LowerFirst(name), command.Name);
            if (plan.HasArgument) view.Add("can" + name, command.Name + " availability query");
            if (plan.IsAsync)
            {
                view.Add("start" + name, command.Name + " operation starter");
                view.Add("start" + name + "WithRequestId", command.Name + " operation starter");
                view.Add("recover" + name + "WithRequestId", command.Name + " operation recovery");
            }
        }
        foreach (var interaction in interactionPlans)
            view.Add(LowerFirst(interaction.Property.Name), interaction.Property.Name + " interaction");

        var interactionSurface = new MemberNames(modelName, "interactions");
        foreach (var interaction in interactionPlans)
            interactionSurface.Add(LowerFirst(interaction.Property.Name), interaction.Property.Name);

        // .NET binds these suffixes under the bridge's route prefix.
        var routes = new MemberNames(modelName, "route");
        routes.Add("Snapshot", "the generated snapshot route");
        routes.Add("Mount", "the View mount route");
        routes.Add("Unmount", "the View unmount route");
        foreach (var property in stateProperties.Where(property => property.SetMethod?.IsPublic == true))
            routes.Add("Set" + property.Name, property.Name + " setter");
        foreach (var property in checkedMembers) routes.Add("Write" + property.Name, property.Name + " checked writer");
        foreach (var command in commandProperties)
        {
            var name = command.Name[..^"Command".Length];
            routes.Add(name, command.Name);
            routes.Add("Can" + name, command.Name + " availability query");
            if (commandPlans[command].IsAsync) routes.Add("Start" + name, command.Name + " operation starter");
        }
    }

    /// <summary>Appends direct AOT-safe codecs used by the generated descriptor.</summary>
    internal static void AppendCSharpCodecs(StringBuilder source, IEnumerable<GeneratedInteractionPlan> plans)
    {
        foreach (var plan in plans)
        {
            plan.Input.AppendCSharpCodec(source, plan.Property.Name + "InteractionInputCodec");
            source.AppendLine();
            plan.Output.AppendCSharpCodec(source, plan.Property.Name + "InteractionOutputCodec");
            source.AppendLine();
        }
    }

    /// <summary>
    /// Returns descriptor expressions for the <c>interactions:</c> base
    /// constructor argument. The descriptor itself remains in the optional
    /// ReactiveUI adapter and core stays ReactiveUI-free.
    /// </summary>
    internal static IEnumerable<string> CSharpDescriptors(IEnumerable<GeneratedInteractionPlan> plans,
        string modelType)
    {
        foreach (var plan in plans)
        {
            var input = plan.Input.RootCSharpType();
            var output = plan.Output.RootCSharpType();
            var inputCodec = plan.Property.Name + "InteractionInputCodec";
            var outputCodec = plan.Property.Name + "InteractionOutputCodec";
            var adapterNamespace = plan.Contract.Flavor is ReactiveUiFlavor.SystemReactive
                ? "global::Runic.Application.Views.ReactiveUI.Reactive"
                : "global::Runic.Application.Views.ReactiveUI";
            yield return $"{adapterNamespace}.ReactiveInteractionDescriptor.Create<{modelType}, {input}, {output}>(\"{LowerFirst(plan.Property.Name)}\", \"{plan.ContractId}\", vm => vm.{plan.Property.Name}, input => global::Runic.Application.Views.BridgeWire.EncodeCanonical(writer => {inputCodec}.Write(writer, input)), element => {outputCodec}.Read(element))";
        }
    }

    /// <summary>Appends the public TypeScript interaction types.</summary>
    internal static void AppendTypeScriptSurface(StringBuilder source, IEnumerable<GeneratedInteractionPlan> plans,
        string shortName)
    {
        var all = plans.ToArray();
        if (all.Length == 0) return;

        source.AppendLine($"export type {shortName}InteractionContext = BridgeInteractionContext;");
        source.AppendLine($"export interface {shortName}Interactions {{");
        foreach (var plan in all)
        {
            source.AppendLine($"  readonly {LowerFirst(plan.Property.Name)}: {{");
            source.AppendLine($"    handle(handler: (input: {plan.Input.TypeScriptType()}, context: {shortName}InteractionContext) => {plan.Output.TypeScriptType()} | Promise<{plan.Output.TypeScriptType()}>): () => void;");
            source.AppendLine("  };");
        }
        source.AppendLine("}");
        source.AppendLine();
    }

    /// <summary>
    /// Appends the module-level <c>interactionDefinitions</c> passed to
    /// <c>connectView</c>: each interaction's contract and its generated input
    /// decoder and output encoder. The shared runtime owns the protocol and
    /// advertises only active handlers, so an unrendered browser control
    /// preserves normal .NET interaction fallback behavior.
    /// </summary>
    internal static void AppendTypeScriptDefinitions(StringBuilder source, IEnumerable<GeneratedInteractionPlan> plans)
    {
        var all = plans.ToArray();
        if (all.Length == 0) return;

        source.AppendLine("const interactionDefinitions = {");
        foreach (var plan in all)
        {
            var typedValue = $"(value as {plan.Output.TypeScriptType()})";
            source.AppendLine($"  {LowerFirst(plan.Property.Name)}: {{ contract: \"{plan.ContractId}\", decodeInput: (value: unknown) => {plan.Input.EmitTypeScriptDecoder("value")}, encodeOutput: (value: unknown) => {plan.Output.EncodeTypeScript(typedValue)} }},");
        }
        source.AppendLine("};");
    }

    private static string LowerFirst(string value) => value.Length == 0 ? value
        : char.ToLowerInvariant(value[0]) + value[1..];

    private sealed class MemberNames(string modelName, string surface)
    {
        private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);

        public void Add(string name, string owner)
        {
            if (_owners.TryGetValue(name, out var previous))
                throw new BridgeNameCollisionException($"{modelName}: generated {surface} name '{name}' conflicts between {previous} and {owner}. Rename one member or add an explicit bridge alias.");
            _owners.Add(name, owner);
        }
    }
}

internal sealed record GeneratedInteractionPlan(PropertyInfo Property, ReactiveInteractionContract Contract,
    BridgeTypeGraph Input, BridgeTypeGraph Output, string ContractId);
