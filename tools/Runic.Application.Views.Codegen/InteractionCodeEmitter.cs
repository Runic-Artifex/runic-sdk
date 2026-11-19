using System.Reflection;
using System.Text;
using static TypeScriptModuleBindings;

/// <summary>
/// Emits the optional browser-facing half of an interaction (a ReactiveUI
/// interaction or a <c>ViewQuestion</c>) and of a <c>ViewRequest</c>.  Keeping
/// this separate from Program makes the interaction protocol reviewable: the
/// main generator merely decides where the generated fragments belong.
/// </summary>
internal static class InteractionCodeEmitter
{
    internal static GeneratedInteractionPlan[] Discover(IEnumerable<PropertyInfo> members, string modelName,
        string contractFingerprint, bool honorJsonIgnore)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractFingerprint);

        var plans = new List<GeneratedInteractionPlan>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var nullability = new NullabilityInfoContext();
        foreach (var property in members)
        {
            var interaction = MvvmCodegenConventions.InspectInteraction(property);
            if (interaction is null) continue;
            if (interaction.ShapeDiagnostic == BridgeDiagnosticCodes.Interaction)
            {
                if (property.GetMethod is null)
                    throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Interaction,
                        $"{modelName}.{property.Name}: an interaction needs a public getter.", property);
                if (property.SetMethod?.IsPublic == true)
                    throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Interaction,
                        $"{modelName}.{property.Name}: interactions are application-owned and cannot have a public setter.", property);
            }
            else RequireGetOnly(property, modelName, interaction.Kind);

            var publicName = LowerFirst(property.Name);
            if (!names.Add(publicName))
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision,
                    $"{modelName}.{property.Name}: duplicate generated interaction name '{publicName}'.", property);
            var timeout = DeclaredTimeout(property, interaction, modelName);
            plans.Add(new GeneratedInteractionPlan(property, interaction,
                BridgeTypeGraph.Discover(interaction.Input,
                    ContractNullability.Argument(property, nullability, [interaction.Input, interaction.Output], 0),
                    $"{modelName}.{property.Name}.input", property, honorJsonIgnore, BridgeValueDirection.ToPage),
                BridgeTypeGraph.Discover(interaction.Output,
                    ContractNullability.Argument(property, nullability, [interaction.Input, interaction.Output], 1),
                    $"{modelName}.{property.Name}.output", property, honorJsonIgnore, BridgeValueDirection.FromPage),
                $"{contractFingerprint}:interaction:{property.Name}", timeout));
        }
        return [.. plans];
    }

    private const string TimeoutAttribute = "Runic.Application.Views.RunicInteractionTimeoutAttribute";

    /// <summary>
    /// The <c>[RunicInteractionTimeout]</c> of a ReactiveUI interaction (sdk#1146), also one on the base property that the
    /// property overrides, as the C# expression the descriptor takes: <c>Timeout.InfiniteTimeSpan</c> or a
    /// <c>TimeSpan</c> of milliseconds. Null without one, for the default of two minutes. Read by name, as
    /// <c>[RunicOperationClose]</c> is. A view question takes its timeout in its constructor instead.
    /// </summary>
    private static string? DeclaredTimeout(PropertyInfo property, MvvmInteraction interaction, string modelName)
    {
        const BindingFlags AllDeclared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic;
        Runic.Application.Views.BridgeAttributeNames.Entry? attribute = null;
        for (var owner = property.DeclaringType; owner is not null && owner != typeof(object) && attribute is null; owner = owner.BaseType)
            if (owner.GetProperty(property.Name, AllDeclared) is { } declared)
                attribute = Runic.Application.Views.BridgeAttributeNames.Find(declared, TimeoutAttribute);
        if (attribute is null) return null;
        if (interaction.ShapeDiagnostic != BridgeDiagnosticCodes.Interaction)
            throw new BridgeDiagnosticException(interaction.ShapeDiagnostic,
                $"{modelName}.{property.Name}: [RunicInteractionTimeout] applies to a ReactiveUI Interaction; a {interaction.Kind} "
                + "takes its timeout in its constructor (new ViewQuestion<TInput, TOutput>(fallback, timeout)).", property);
        var milliseconds = attribute.FixedArguments?.Invoke() is [var argument]
            ? Convert.ToInt64(argument, System.Globalization.CultureInfo.InvariantCulture) : 0;
        if (milliseconds == System.Threading.Timeout.Infinite) return "global::System.Threading.Timeout.InfiniteTimeSpan";
        if (milliseconds is <= 0 or > MaximumTimeoutMilliseconds)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Interaction,
                $"{modelName}.{property.Name}: [RunicInteractionTimeout({milliseconds})] is not a timeout; use 1 to "
                + $"{MaximumTimeoutMilliseconds} milliseconds (24 hours), or Timeout.Infinite for no deadline.", property);
        return $"global::System.TimeSpan.FromMilliseconds({milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
    }

    // RunicInteractionTimeoutAttribute.MaximumMilliseconds.
    private const long MaximumTimeoutMilliseconds = 86_400_000;

    /// <summary>
    /// The <c>ViewRequest&lt;T&gt;</c> members of a ViewModel: one-way requests to the page, each with its input
    /// codec and its contract <c>{fingerprint}:request:{Property}</c> (specs/application, "View requests").
    /// </summary>
    internal static GeneratedViewRequestPlan[] DiscoverRequests(IEnumerable<PropertyInfo> members, string modelName,
        string contractFingerprint, bool honorJsonIgnore)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractFingerprint);

        var plans = new List<GeneratedViewRequestPlan>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var nullability = new NullabilityInfoContext();
        foreach (var property in members)
        {
            if (Runic.Application.Views.RunicViewConventions.ViewRequest(property.PropertyType) is not { } request) continue;
            RequireGetOnly(property, modelName, "view request");
            var publicName = LowerFirst(property.Name);
            if (!names.Add(publicName))
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision,
                    $"{modelName}.{property.Name}: duplicate generated view request name '{publicName}'.", property);
            var input = request.GenericTypeArguments[0];
            plans.Add(new GeneratedViewRequestPlan(property,
                BridgeTypeGraph.Discover(input, ContractNullability.Argument(property, nullability, [input], 0),
                    $"{modelName}.{property.Name}.input", property, honorJsonIgnore, BridgeValueDirection.ToPage),
                $"{contractFingerprint}:request:{property.Name}"));
        }
        return [.. plans];
    }

    // A ViewRequest or ViewQuestion is application-owned: the generated Bridge attaches the instance it reads once.
    private static void RequireGetOnly(PropertyInfo property, string modelName, string kind)
    {
        if (property.GetMethod?.IsPublic != true)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ViewMember,
                $"{modelName}.{property.Name}: a {kind} needs a public getter. Expose it as a get-only property, such as {{ get; }} = new(...).", property);
        if (property.SetMethod?.IsPublic == true)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ViewMember,
                $"{modelName}.{property.Name}: a {kind} is application-owned and cannot have a public setter. Expose it as a get-only property, such as {{ get; }} = new(...).", property);
    }

    /// <summary>
    /// Checks every generated TypeScript state/view member before emitting any
    /// files. A collision is otherwise a surprisingly easy way to ship a client
    /// whose declaration looks valid but whose object literal overwrites a
    /// method or whose JSON state has duplicate keys. Requests name their kind
    /// and member, so .NET has no route names that could collide.
    /// </summary>
    internal static void ValidatePublicSurface(string modelName, IEnumerable<PropertyInfo> properties,
        IEnumerable<PropertyInfo> commands, IReadOnlyDictionary<PropertyInfo, GeneratedCommandPlan> commandPlans,
        IEnumerable<GeneratedInteractionPlan> interactions, IEnumerable<GeneratedViewRequestPlan> requests,
        IEnumerable<PropertyInfo> checkedProperties, Func<PropertyInfo, string> wireName, bool hasErrors, bool hasValidation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(commandPlans);
        ArgumentNullException.ThrowIfNull(interactions);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(checkedProperties);
        ArgumentNullException.ThrowIfNull(wireName);

        var stateProperties = properties.ToArray();
        var commandProperties = commands.ToArray();
        var checkedMembers = checkedProperties.ToArray();
        var state = new MemberNames(modelName, "state");
        if (hasValidation) state.Add("validation", "the generated validation state");
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
        // A command's bare name is a client member; a few valid C# names are
        // not usable there (a construct signature, a thenable client, ...).
        foreach (var command in commandProperties)
        {
            var name = command.Name[..^"Command".Length];
            if (TypeScriptModules.ReservedClientMemberNames.TryGetValue(LowerFirst(name), out var reserved))
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision,
                    $"{modelName}.{command.Name}: the generated client member '{LowerFirst(name)}' {reserved.Reason}. Rename the command, for example to {reserved.Suggestion}Command (client member '{LowerFirst(reserved.Suggestion)}').",
                    command);
        }
        var interactionPlans = interactions.ToArray();
        if (interactionPlans.Length > 0) view.Add("interactions", "the generated interactions property");
        var requestPlans = requests.ToArray();
        if (requestPlans.Length > 0) view.Add("requests", "the generated requests property");
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
                view.Add("recover" + name, command.Name + " operation recovery");
            }
        }
        foreach (var interaction in interactionPlans)
            view.Add(LowerFirst(interaction.Property.Name), interaction.Property.Name + " interaction");
        // A request's name addresses it in the producer beside the interactions, so it is unique among them too.
        foreach (var request in requestPlans)
            view.Add(LowerFirst(request.Property.Name), request.Property.Name + " view request");

        var interactionSurface = new MemberNames(modelName, "interactions");
        foreach (var interaction in interactionPlans)
            interactionSurface.Add(LowerFirst(interaction.Property.Name), interaction.Property.Name);
        var requestSurface = new MemberNames(modelName, "requests");
        foreach (var request in requestPlans)
            requestSurface.Add(LowerFirst(request.Property.Name), request.Property.Name);

    }

    /// <summary>Names the direct AOT-safe codecs that the generated descriptors use.</summary>
    internal static void AddCSharpCodecs(BridgeCodecAliases codecs, IEnumerable<GeneratedInteractionPlan> plans)
    {
        foreach (var plan in plans)
        {
            codecs.Add(plan.Property.Name + "InteractionInputCodec", plan.Input);
            codecs.Add(plan.Property.Name + "InteractionOutputCodec", plan.Output);
        }
    }

    /// <summary>Names the write-only input codec of each view request.</summary>
    internal static void AddCSharpRequestCodecs(BridgeCodecAliases codecs, IEnumerable<GeneratedViewRequestPlan> plans)
    {
        foreach (var plan in plans)
            codecs.Add(plan.Property.Name + "RequestInputCodec", plan.Input);
    }

    /// <summary>
    /// Returns the view requests' descriptor expressions for the <c>interactions:</c> base constructor argument,
    /// which the runtime provides in both MVVM flavors.
    /// </summary>
    internal static IEnumerable<string> CSharpRequestDescriptors(IEnumerable<GeneratedViewRequestPlan> plans, string modelType)
    {
        foreach (var plan in plans)
            yield return $"global::Runic.Application.Views.Generated.ViewRequestDescriptor.Create<{plan.Input.RootCSharpType()}>(\"{LowerFirst(plan.Property.Name)}\", \"{plan.ContractId}\", static vm => {GeneratedModel.Access(modelType)}.{plan.Property.Name}, input => global::Runic.Application.Views.Generated.BridgeWire.EncodeCanonical(writer => {plan.Property.Name}RequestInputCodec.Write(writer, input)))";
    }

    /// <summary>Appends the public TypeScript view request types. <paramref name="mark"/> ends a member's line (#952).</summary>
    internal static void AppendTypeScriptRequestSurface(StringBuilder source, IEnumerable<GeneratedViewRequestPlan> plans,
        string shortName, Func<string, string>? mark = null)
    {
        var all = plans.ToArray();
        if (all.Length == 0) return;

        source.AppendLine($"export type {shortName}ViewRequestContext = {Ref("BridgeViewRequestContext")};");
        source.AppendLine($"export interface {shortName}ViewRequests {{");
        foreach (var plan in all)
        {
            XmlDocumentation.Append(source, "  ", XmlDocumentation.Summary(plan.Property));
            source.AppendLine($"  readonly {LowerFirst(plan.Property.Name)}: {{ handle(handler: (input: {plan.Input.TypeScriptType()}, context: {shortName}ViewRequestContext) => void | Promise<void>): () => void }};{mark?.Invoke(LowerFirst(plan.Property.Name))}");
        }
        source.AppendLine("}");
        source.AppendLine();
    }

    /// <summary>
    /// Appends the module-level <c>requestDefinitions</c> passed to <c>connectView</c>: each view request's contract
    /// and its generated input decoder.
    /// </summary>
    internal static void AppendTypeScriptRequestDefinitions(StringBuilder source, IEnumerable<GeneratedViewRequestPlan> plans)
    {
        var all = plans.ToArray();
        if (all.Length == 0) return;

        source.AppendLine($"const {Ref("requestDefinitions")} = {Ref("defineRequests")}({{");
        foreach (var plan in all)
            source.AppendLine($"  {LowerFirst(plan.Property.Name)}: {{ contract: \"{plan.ContractId}\", decodeInput: (value: unknown) => {BridgeTypeGraph.ArrowBody(plan.Input.EmitTypeScriptDecoder("value"))} }},");
        source.AppendLine("});");
    }

    /// <summary>
    /// Returns descriptor expressions for the <c>interactions:</c> base
    /// constructor argument. The library convention that recognised the
    /// interaction emits a call into its own adapter, so core stays
    /// ReactiveUI-free.
    /// </summary>
    internal static IEnumerable<string> CSharpDescriptors(IEnumerable<GeneratedInteractionPlan> plans,
        string modelType)
    {
        foreach (var plan in plans)
        {
            var inputCodec = plan.Property.Name + "InteractionInputCodec";
            var outputCodec = plan.Property.Name + "InteractionOutputCodec";
            yield return plan.Contract.Descriptor(plan, modelType, LowerFirst(plan.Property.Name), inputCodec, outputCodec);
        }
    }

    /// <summary>Appends the public TypeScript interaction types. <paramref name="mark"/> ends a member's line (#952).</summary>
    internal static void AppendTypeScriptSurface(StringBuilder source, IEnumerable<GeneratedInteractionPlan> plans,
        string shortName, Func<string, string>? mark = null)
    {
        var all = plans.ToArray();
        if (all.Length == 0) return;

        source.AppendLine($"export type {shortName}InteractionContext = {Ref("BridgeInteractionContext")};");
        source.AppendLine($"export interface {shortName}Interactions {{");
        foreach (var plan in all)
        {
            XmlDocumentation.Append(source, "  ", XmlDocumentation.Summary(plan.Property));
            source.AppendLine($"  readonly {LowerFirst(plan.Property.Name)}: {{{mark?.Invoke(LowerFirst(plan.Property.Name))}");
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

        source.AppendLine($"const {Ref("interactionDefinitions")} = {Ref("defineInteractions")}({{");
        foreach (var plan in all)
        {
            var typedValue = $"(value as {plan.Output.TypeScriptType()})";
            source.AppendLine($"  {LowerFirst(plan.Property.Name)}: {{ contract: \"{plan.ContractId}\", decodeInput: (value: unknown) => {BridgeTypeGraph.ArrowBody(plan.Input.EmitTypeScriptDecoder("value"))}, encodeOutput: (value: unknown) => {BridgeTypeGraph.ArrowBody(plan.Output.EncodeTypeScript(typedValue))} }},");
        }
        source.AppendLine("});");
    }

    /// <summary>
    /// Appends <c>{Name}ConnectOptions</c>: the connect options with typed interaction handlers, which the client
    /// registers before <c>open</c> and sends with it.
    /// </summary>
    internal static void AppendConnectOptions(StringBuilder source, IEnumerable<GeneratedInteractionPlan> plans, string shortName)
    {
        source.AppendLine($"/** Options of `connect{shortName}`. */");
        source.AppendLine($"export interface {shortName}ConnectOptions extends {Ref("BridgeConnectOptions")} {{");
        source.AppendLine("  /**");
        source.AppendLine("   * Interaction handlers, registered before the client opens the View, so .NET can ask them from the first");
        source.AppendLine("   * request on. Handlers registered later through `interactions` replace them.");
        source.AppendLine("   */");
        source.AppendLine("  readonly handlers?: {");
        foreach (var plan in plans)
            source.AppendLine($"    readonly {LowerFirst(plan.Property.Name)}?: (input: {plan.Input.TypeScriptType()}, context: {shortName}InteractionContext) => {plan.Output.TypeScriptType()} | Promise<{plan.Output.TypeScriptType()}>;");
        source.AppendLine("  };");
        source.AppendLine("}");
    }

    private static string LowerFirst(string value) => value.Length == 0 ? value
        : char.ToLowerInvariant(value[0]) + value[1..];

    private sealed class MemberNames(string modelName, string surface)
    {
        private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);

        public void Add(string name, string owner)
        {
            if (_owners.TryGetValue(name, out var previous))
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision, $"{modelName}: generated {surface} name '{name}' conflicts between {previous} and {owner}. Rename one member or add an explicit bridge alias.");
            _owners.Add(name, owner);
        }
    }
}

// Timeout: the C# expression of a [RunicInteractionTimeout], or null for the default.
internal sealed record GeneratedInteractionPlan(PropertyInfo Property, MvvmInteraction Contract,
    BridgeTypeGraph Input, BridgeTypeGraph Output, string ContractId, string? Timeout = null);

internal sealed record GeneratedViewRequestPlan(PropertyInfo Property, BridgeTypeGraph Input, string ContractId);
