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
                BridgeTypeGraph.Discover(interaction.Input, rootPath: $"{modelName}.{property.Name}.input"),
                BridgeTypeGraph.Discover(interaction.Output, rootPath: $"{modelName}.{property.Name}.output"),
                $"{contractFingerprint}:interaction:{property.Name}"));
        }
        return [.. plans];
    }

    /// <summary>
    /// Checks generated TypeScript member names before emitting any files. A
    /// collision is otherwise a surprisingly easy way to ship a client whose
    /// declaration looks valid but whose object literal overwrites a method.
    /// </summary>
    internal static void ValidatePublicSurface(string modelName, IEnumerable<PropertyInfo> properties,
        IEnumerable<PropertyInfo> commands, IReadOnlyDictionary<PropertyInfo, GeneratedCommandPlan> commandPlans,
        IEnumerable<GeneratedInteractionPlan> interactions, IEnumerable<PropertyInfo> checkedProperties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(commandPlans);
        ArgumentNullException.ThrowIfNull(interactions);
        ArgumentNullException.ThrowIfNull(checkedProperties);

        var state = new MemberNames(modelName, "state");
        state.Add("revision", "the generated revision field");
        foreach (var property in properties) state.Add(LowerFirst(property.Name), property.Name);
        foreach (var command in commands)
        {
            var plan = commandPlans[command];
            var name = command.Name[..^"Command".Length];
            if (!plan.HasArgument) state.Add("can" + name, command.Name + " availability");
            if (plan.ReactiveContract is not null) state.Add("is" + name + "Executing", command.Name + " execution state");
        }

        var view = new MemberNames(modelName, "view");
        view.Add("snapshot", "the generated snapshot property");
        view.Add("subscribe", "the generated subscribe method");
        view.Add("dispose", "the generated dispose method");
        var interactionPlans = interactions.ToArray();
        if (interactionPlans.Length > 0) view.Add("interactions", "the generated interactions property");
        foreach (var property in properties.Where(property => property.SetMethod?.IsPublic == true))
            view.Add("set" + property.Name, property.Name + " setter");
        var checkedMembers = checkedProperties.ToArray();
        foreach (var property in checkedMembers) view.Add("write" + property.Name, property.Name + " checked writer");
        if (checkedMembers.Length > 0) view.Add("fieldBaseline", "the generated checked-field baseline method");
        foreach (var command in commands)
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
            var input = BridgeTypeGraph.CSharpType(plan.Contract.Input);
            var output = BridgeTypeGraph.CSharpType(plan.Contract.Output);
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

        source.AppendLine($"export interface {shortName}InteractionContext {{");
        source.AppendLine("  readonly signal: AbortSignal;");
        source.AppendLine("}");
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
    /// Appends lexical TypeScript used from within <c>connectXAt</c>. Emit the
    /// setup after a mount acknowledgement, call the returned cleanup block at
    /// the beginning of <c>dispose</c>, and expose <c>interactions</c> in the
    /// returned view. The helpers deliberately advertise only active handlers,
    /// so an unrendered browser control preserves normal .NET interaction
    /// fallback behavior.
    /// </summary>
    internal static InteractionTypeScriptFragments TypeScriptRuntime(IEnumerable<GeneratedInteractionPlan> plans,
        string shortName)
    {
        var all = plans.ToArray();
        if (all.Length == 0) return InteractionTypeScriptFragments.Empty;

        var setup = new StringBuilder();
        setup.AppendLine("  type InteractionHandler = { readonly name: string; readonly contract: string; readonly generation: number; readonly controller: AbortController; readonly handle: (input: unknown, context: " + shortName + "InteractionContext) => unknown | Promise<unknown> };");
        setup.AppendLine("  const interactionHandlers = new Map<string, InteractionHandler>();");
        setup.AppendLine("  let interactionGeneration = 0;");
        setup.AppendLine("  let interactionCapabilityGeneration = 0;");
        setup.AppendLine("  let interactionDisposed = false;");
        setup.AppendLine("  let interactionUnavailable = false;");
        setup.AppendLine("  let interactionLoop: Promise<void> | undefined;");
        setup.AppendLine("  let interactionControlLoop: Promise<void> | undefined;");
        setup.AppendLine("  let interactionCapabilitySync: Promise<void> = Promise.resolve();");
        setup.AppendLine("  let interactionRetry: Promise<void> | undefined;");
        setup.AppendLine("  let interactionRetryTimer: ReturnType<typeof setTimeout> | undefined;");
        setup.AppendLine("  let interactionRetryResolve: (() => void) | undefined;");
        setup.AppendLine("  let interactionRetryDelay = 25;");
        setup.AppendLine("  const activeInteractionRequests = new Map<string, AbortController>();");
        setup.AppendLine("  const cancelledInteractionRequests = new Map<string, ReturnType<typeof setTimeout>>();");
        setup.AppendLine("  const interactionPresentationId = mountToken;");
        setup.AppendLine("  function interactionIdentity(name: string, contract: string): string { return `${name.length}:${name}${contract.length}:${contract}`; }");
        setup.AppendLine("  function interactionContract(name: string): string {");
        setup.AppendLine("    switch (name) {");
        foreach (var plan in all)
            setup.AppendLine($"      case \"{LowerFirst(plan.Property.Name)}\": return \"{plan.ContractId}\";");
        setup.AppendLine("      default: throw new BridgeError(\"failed\", \"Unknown interaction surface.\");");
        setup.AppendLine("    }");
        setup.AppendLine("  }");
        setup.AppendLine("  function jsonForInteraction(value: unknown): string {");
        setup.AppendLine("    const json = JSON.stringify(value, (_key, item) => typeof item === \"bigint\" ? item.toString() : item);");
        setup.AppendLine("    if (json === undefined) throw new BridgeError(\"failed\", \"The interaction payload is not JSON serializable.\");");
        setup.AppendLine("    return json;");
        setup.AppendLine("  }");
        setup.AppendLine("  async function replyInteraction(request: { readonly requestId: string; readonly route: string; readonly presentationId: string; readonly ownerEpoch: number; readonly name: string; readonly contract: string }, kind: \"answered\" | \"cancelled\" | \"failed\", output?: unknown): Promise<void> {");
        setup.AppendLine("    const payload: Record<string, unknown> = { kind, requestId: request.requestId, route: request.route, presentationId: request.presentationId, ownerEpoch: request.ownerEpoch, name: request.name, contract: request.contract };");
        setup.AppendLine("    if (kind === \"answered\") payload.output = output;");
        setup.AppendLine("    try { await bridge.call(\"__runicInteractionReply\", jsonForInteraction(payload)); } catch { /* The request will be cancelled by its presentation lifecycle. */ }");
        setup.AppendLine("  }");
        setup.AppendLine("  function abortActiveInteractions(): void {");
        setup.AppendLine("    for (const controller of activeInteractionRequests.values()) controller.abort();");
        setup.AppendLine("    activeInteractionRequests.clear();");
        setup.AppendLine("    for (const timer of cancelledInteractionRequests.values()) clearTimeout(timer);");
        setup.AppendLine("    cancelledInteractionRequests.clear();");
        setup.AppendLine("  }");
        setup.AppendLine("  function cancelInteractionRequest(requestId: string): void {");
        setup.AppendLine("    const active = activeInteractionRequests.get(requestId); if (active) { active.abort(); return; }");
        setup.AppendLine("    if (cancelledInteractionRequests.has(requestId)) return;");
        setup.AppendLine("    if (cancelledInteractionRequests.size >= 32) { const oldest = cancelledInteractionRequests.keys().next().value as string | undefined; if (oldest) { const timer = cancelledInteractionRequests.get(oldest); if (timer) clearTimeout(timer); cancelledInteractionRequests.delete(oldest); } }");
        setup.AppendLine("    const timer = setTimeout(() => cancelledInteractionRequests.delete(requestId), 120_000);");
        setup.AppendLine("    cancelledInteractionRequests.set(requestId, timer);");
        setup.AppendLine("  }");
        setup.AppendLine("  function stopInteractionLoops(): void { interactionUnavailable = true; abortActiveInteractions(); interactionRetryResolve?.(); }");
        setup.AppendLine("  function waitForInteractionRetry(): Promise<void> {");
        setup.AppendLine("    if (interactionRetry) return interactionRetry;");
        setup.AppendLine("    const delay = interactionRetryDelay; interactionRetryDelay = Math.min(interactionRetryDelay * 2, 500);");
        setup.AppendLine("    interactionRetry = new Promise<void>(resolve => {");
        setup.AppendLine("      const finish = () => { if (interactionRetryTimer) clearTimeout(interactionRetryTimer); interactionRetryTimer = undefined; interactionRetryResolve = undefined; interactionRetry = undefined; resolve(); };");
        setup.AppendLine("      interactionRetryResolve = finish; interactionRetryTimer = setTimeout(finish, delay);");
        setup.AppendLine("    });");
        setup.AppendLine("    return interactionRetry;");
        setup.AppendLine("  }");
        setup.AppendLine("  function syncInteractionCapabilities(): void {");
        setup.AppendLine("    if (!interactionPresentationId) return;");
        setup.AppendLine("    const generation = ++interactionCapabilityGeneration;");
        setup.AppendLine("    const handlers = [...interactionHandlers.values()].map(handler => ({ name: handler.name, contract: handler.contract }));");
        setup.AppendLine("    interactionCapabilitySync = interactionCapabilitySync.catch(() => undefined).then(async () => {");
        setup.AppendLine("      if (interactionDisposed || interactionUnavailable || lease.disposed || !isLive() || !bridge.isConnected()) return;");
        setup.AppendLine("      const reply = JSON.parse(await bridge.call(\"__runicInteractionControl\", jsonForInteraction({ route, presentationId: interactionPresentationId, generation, handlers }))) as { kind?: unknown };");
        setup.AppendLine("      if (reply?.kind !== \"ok\") { interactionUnavailable = true; abortActiveInteractions(); throw new BridgeError(\"disconnected\", \"The interaction presentation is no longer available.\"); }");
        setup.AppendLine("    });");
        setup.AppendLine("    void interactionCapabilitySync.catch(() => undefined);");
        setup.AppendLine("  }");
        setup.AppendLine("  function decodeInteractionInput(name: string, value: unknown): unknown {");
        setup.AppendLine("    switch (name) {");
        foreach (var plan in all)
            setup.AppendLine($"      case \"{LowerFirst(plan.Property.Name)}\": return {plan.Input.EmitTypeScriptDecoder("value")};");
        setup.AppendLine("      default: throw new BridgeError(\"failed\", \"Unknown interaction input.\");");
        setup.AppendLine("    }");
        setup.AppendLine("  }");
        setup.AppendLine("  function encodeInteractionOutput(name: string, value: unknown): unknown {");
        setup.AppendLine("    switch (name) {");
        foreach (var plan in all)
        {
            var typedValue = $"(value as {plan.Output.TypeScriptType()})";
            setup.AppendLine($"      case \"{LowerFirst(plan.Property.Name)}\": return {plan.Output.EncodeTypeScript(typedValue)};");
        }
        setup.AppendLine("      default: throw new BridgeError(\"failed\", \"Unknown interaction output.\");");
        setup.AppendLine("    }");
        setup.AppendLine("  }");
        setup.AppendLine("  async function handleInteractionRequest(request: Record<string, unknown>): Promise<void> {");
        setup.AppendLine("    if (typeof request.requestId !== \"string\" || typeof request.route !== \"string\" || typeof request.presentationId !== \"string\" || typeof request.ownerEpoch !== \"number\" || !Number.isSafeInteger(request.ownerEpoch) || typeof request.name !== \"string\" || typeof request.contract !== \"string\") return;");
        setup.AppendLine("    const identity = { requestId: request.requestId, route: request.route, presentationId: request.presentationId, ownerEpoch: request.ownerEpoch, name: request.name, contract: request.contract };");
        setup.AppendLine("    const key = interactionIdentity(request.name, request.contract);");
        setup.AppendLine("    const handler = interactionHandlers.get(key);");
        setup.AppendLine("    if (!handler || request.route !== route || request.presentationId !== interactionPresentationId) { await replyInteraction(identity, \"cancelled\"); return; }");
        setup.AppendLine("    const controller = new AbortController();");
        setup.AppendLine("    const expiresAt = typeof request.expiresAt === \"string\" ? Date.parse(request.expiresAt) : Number.NaN;");
        setup.AppendLine("    const deadline = Number.isFinite(expiresAt) && expiresAt > Date.now() ? setTimeout(() => controller.abort(), Math.min(expiresAt - Date.now(), 600_000)) : undefined;");
        setup.AppendLine("    const abortFromHandler = () => controller.abort();");
        setup.AppendLine("    handler.controller.signal.addEventListener(\"abort\", abortFromHandler, { once: true });");
        setup.AppendLine("    activeInteractionRequests.set(identity.requestId, controller);");
        setup.AppendLine("    if (Number.isFinite(expiresAt) && expiresAt <= Date.now()) controller.abort();");
        setup.AppendLine("    const cancelledBeforeDelivery = cancelledInteractionRequests.get(identity.requestId);");
        setup.AppendLine("    if (cancelledBeforeDelivery) { clearTimeout(cancelledBeforeDelivery); cancelledInteractionRequests.delete(identity.requestId); controller.abort(); }");
        setup.AppendLine("    try {");
        setup.AppendLine("      const input = decodeInteractionInput(request.name, request.input);");
        setup.AppendLine("      const output = await handler.handle(input, { signal: controller.signal });");
        setup.AppendLine("      if (interactionDisposed || lease.disposed || !isLive() || controller.signal.aborted || interactionHandlers.get(key) !== handler) await replyInteraction(identity, \"cancelled\");");
        setup.AppendLine("      else await replyInteraction(identity, \"answered\", encodeInteractionOutput(request.name, output));");
        setup.AppendLine("    } catch {");
        setup.AppendLine("      await replyInteraction(identity, controller.signal.aborted ? \"cancelled\" : \"failed\");");
        setup.AppendLine("    } finally {");
        setup.AppendLine("      handler.controller.signal.removeEventListener(\"abort\", abortFromHandler);");
        setup.AppendLine("      if (deadline) clearTimeout(deadline);");
        setup.AppendLine("      if (activeInteractionRequests.get(identity.requestId) === controller) activeInteractionRequests.delete(identity.requestId);");
        setup.AppendLine("    }");
        setup.AppendLine("  }");
        setup.AppendLine("  async function runInteractionLoop(): Promise<void> {");
        setup.AppendLine("    if (!interactionPresentationId) return;");
        setup.AppendLine("    while (!interactionDisposed && !interactionUnavailable && !lease.disposed && isLive() && bridge.isConnected() && interactionHandlers.size !== 0) {");
        setup.AppendLine("      try { await interactionCapabilitySync; } catch { if (interactionUnavailable || !bridge.isConnected()) { stopInteractionLoops(); return; } await waitForInteractionRetry(); syncInteractionCapabilities(); continue; }");
        setup.AppendLine("      if (interactionDisposed || interactionUnavailable || lease.disposed || !isLive() || !bridge.isConnected() || interactionHandlers.size === 0) return;");
        setup.AppendLine("      const handlers = [...interactionHandlers.values()].map(handler => ({ name: handler.name, contract: handler.contract }));");
        setup.AppendLine("      let envelope: unknown;");
        setup.AppendLine("      try { envelope = JSON.parse(await bridge.call(\"__runicInteractionWait\", jsonForInteraction({ route, presentationId: interactionPresentationId, generation: interactionCapabilityGeneration, handlers }))); }");
        setup.AppendLine("      catch { if (!bridge.isConnected()) { stopInteractionLoops(); return; } await waitForInteractionRetry(); continue; }");
        setup.AppendLine("      if (interactionDisposed || interactionUnavailable || lease.disposed || !isLive() || !bridge.isConnected() || interactionHandlers.size === 0) return;");
        setup.AppendLine("      interactionRetryDelay = 25;");
        setup.AppendLine("      if (envelope === null || typeof envelope !== \"object\") continue;");
        setup.AppendLine("      const request = envelope as Record<string, unknown>;");
        setup.AppendLine("      if (request.kind === \"disconnected\" || request.kind === \"ignored\" || request.kind === \"unsupported\" || request.kind === \"invalid-request\" || request.kind === \"cancelled\") { stopInteractionLoops(); return; }");
        setup.AppendLine("      if (request.kind === \"request\") void handleInteractionRequest(request);");
        setup.AppendLine("    }");
        setup.AppendLine("  }");
        setup.AppendLine("  async function runInteractionControlLoop(): Promise<void> {");
        setup.AppendLine("    if (!interactionPresentationId) return;");
        setup.AppendLine("    while (!interactionDisposed && !interactionUnavailable && !lease.disposed && isLive() && bridge.isConnected() && interactionHandlers.size !== 0) {");
        setup.AppendLine("      let envelope: unknown;");
        setup.AppendLine("      try { envelope = JSON.parse(await bridge.call(\"__runicInteractionControlWait\", jsonForInteraction({ route, presentationId: interactionPresentationId }))); }");
        setup.AppendLine("      catch { if (!bridge.isConnected()) { stopInteractionLoops(); return; } await waitForInteractionRetry(); continue; }");
        setup.AppendLine("      if (interactionDisposed || interactionUnavailable || lease.disposed || !isLive() || !bridge.isConnected() || interactionHandlers.size === 0) return;");
        setup.AppendLine("      interactionRetryDelay = 25;");
        setup.AppendLine("      if (envelope === null || typeof envelope !== \"object\") continue;");
        setup.AppendLine("      const control = envelope as Record<string, unknown>;");
        setup.AppendLine("      if (control.kind === \"disconnected\" || control.kind === \"ignored\" || control.kind === \"invalid-request\" || (control.kind === \"cancelled\" && typeof control.requestId !== \"string\")) { stopInteractionLoops(); return; }");
        setup.AppendLine("      if (control.kind === \"cancelled\" && typeof control.requestId === \"string\") cancelInteractionRequest(control.requestId);");
        setup.AppendLine("    }");
        setup.AppendLine("  }");
        setup.AppendLine("  function ensureInteractionLoop(): void {");
        setup.AppendLine("    if (interactionDisposed || interactionUnavailable || lease.disposed || interactionHandlers.size === 0) return;");
        setup.AppendLine("    if (!interactionLoop) interactionLoop = runInteractionLoop().finally(() => { interactionLoop = undefined; if (!interactionDisposed && !interactionUnavailable && !lease.disposed && isLive() && bridge.isConnected() && interactionHandlers.size !== 0) ensureInteractionLoop(); });");
        setup.AppendLine("    if (!interactionControlLoop) interactionControlLoop = runInteractionControlLoop().finally(() => { interactionControlLoop = undefined; if (!interactionDisposed && !interactionUnavailable && !lease.disposed && isLive() && bridge.isConnected() && interactionHandlers.size !== 0) ensureInteractionLoop(); });");
        setup.AppendLine("  }");
        setup.AppendLine($"  const interactions: {shortName}Interactions = {{");
        foreach (var plan in all)
        {
            var name = LowerFirst(plan.Property.Name);
            setup.AppendLine($"    {name}: {{");
            setup.AppendLine("      handle(handler) {");
            setup.AppendLine("        if (interactionDisposed || interactionUnavailable || lease.disposed || !isLive() || !bridge.isConnected()) throw new BridgeError(\"disconnected\", \"The interaction view is disconnected.\");");
            setup.AppendLine($"        const key = interactionIdentity(\"{name}\", interactionContract(\"{name}\"));");
            setup.AppendLine("        const previous = interactionHandlers.get(key);");
            setup.AppendLine("        previous?.controller.abort();");
            setup.AppendLine($"        const registered: InteractionHandler = {{ name: \"{name}\", contract: interactionContract(\"{name}\"), generation: ++interactionGeneration, controller: new AbortController(), handle: handler as InteractionHandler[\"handle\"] }};");
            setup.AppendLine("        interactionHandlers.set(key, registered);");
            setup.AppendLine("        syncInteractionCapabilities();");
            setup.AppendLine("        ensureInteractionLoop();");
            setup.AppendLine("        return () => { if (interactionHandlers.get(key) === registered) { interactionHandlers.delete(key); registered.controller.abort(); syncInteractionCapabilities(); } };");
            setup.AppendLine("      },");
            setup.AppendLine("    },");
        }
        setup.AppendLine("  };");

        return new InteractionTypeScriptFragments(setup.ToString(),
            "    interactionDisposed = true;\n    stopInteractionLoops();\n    for (const handler of interactionHandlers.values()) handler.controller.abort();\n    interactionHandlers.clear();\n    syncInteractionCapabilities();",
            "    interactions,");
    }

    private static string LowerFirst(string value) => value.Length == 0 ? value
        : char.ToLowerInvariant(value[0]) + value[1..];

    private sealed class MemberNames(string modelName, string surface)
    {
        private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);

        public void Add(string name, string owner)
        {
            if (_owners.TryGetValue(name, out var previous))
                throw new NotSupportedException($"{modelName}: generated {surface} member '{name}' conflicts between {previous} and {owner}. Rename one member or add an explicit bridge alias.");
            _owners.Add(name, owner);
        }
    }
}

internal sealed record GeneratedInteractionPlan(PropertyInfo Property, ReactiveInteractionContract Contract,
    BridgeTypeGraph Input, BridgeTypeGraph Output, string ContractId);

internal sealed record InteractionTypeScriptFragments(string Setup, string Dispose, string ViewMember)
{
    internal static InteractionTypeScriptFragments Empty { get; } = new(string.Empty, string.Empty, string.Empty);
}
