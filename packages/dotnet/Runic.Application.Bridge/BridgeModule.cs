using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Runic.Application.Bridge;

/// <summary>Deterministic build-time metadata emitted by the bridge generator.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class BridgeModuleAttribute(string metadata) : Attribute
{
    /// <summary>Gets the generated module metadata.</summary>
    public string Metadata { get; } = metadata;
}

/// <summary>Generated module dispatch delegates; populated without runtime discovery.</summary>
public sealed class BridgeModuleRegistry
{
    private readonly Dictionary<string, Func<IServiceProvider, JsonElement, BridgeCommandContext, CancellationToken, ValueTask<BridgeDispatchResult>>> _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<JsonElement, JsonElement>> _errors = new(StringComparer.Ordinal);
    private Func<IServiceProvider, BridgeSnapshotContext, CancellationToken, ValueTask<JsonElement>>? _snapshot;

    /// <summary>Registers a generated command adapter.</summary>
    public void AddCommand(string tag, Func<IServiceProvider, JsonElement, BridgeCommandContext, CancellationToken, ValueTask<BridgeDispatchResult>> dispatch) => _commands.Add(tag, dispatch);
    /// <summary>Registers a strict error codec.</summary>
    public void AddError(string tag, Func<JsonElement, JsonElement> validate) => _errors.Add(tag, validate);
    /// <summary>Registers the sole snapshot adapter.</summary>
    public void SetSnapshot(Func<IServiceProvider, BridgeSnapshotContext, CancellationToken, ValueTask<JsonElement>> snapshot)
    {
        if (_snapshot is not null) throw new InvalidOperationException("The application has multiple snapshot providers.");
        _snapshot = snapshot;
    }
    internal ValueTask<JsonElement> Snapshot(IServiceProvider services, BridgeSnapshotContext context, CancellationToken cancellationToken) =>
        (_snapshot ?? throw new InvalidOperationException("The application has no snapshot provider."))(services, context, cancellationToken);
    internal ValueTask<BridgeDispatchResult> Dispatch(IServiceProvider services, JsonElement command, BridgeCommandContext context, CancellationToken cancellationToken)
    {
        if (command.ValueKind != JsonValueKind.Object || !command.TryGetProperty("_tag", out JsonElement tag) || tag.ValueKind != JsonValueKind.String || !_commands.TryGetValue(tag.GetString()!, out var dispatch))
            throw new JsonException("The command tag is not declared by the contract.");
        return dispatch(services, command, context, cancellationToken);
    }
    internal JsonElement ValidateError(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object || !error.TryGetProperty("_tag", out JsonElement tag) || tag.ValueKind != JsonValueKind.String || !_errors.TryGetValue(tag.GetString()!, out var validate))
            throw new JsonException("The error tag is not declared by the contract.");
        return validate(error);
    }
}

/// <summary>Session-scoped dispatch over statically generated module delegates.</summary>
public sealed class MemberApplicationBridgeDispatcher(
    string protocolIdentity, int protocolVersion, string fingerprint, IServiceProvider services, BridgeModuleRegistry modules) : IApplicationBridgeDispatcher
{
    /// <inheritdoc />
    public string ProtocolIdentity { get; } = protocolIdentity;
    /// <inheritdoc />
    public int ProtocolVersion { get; } = protocolVersion;
    /// <inheritdoc />
    public string ManifestFingerprint { get; } = fingerprint;
    /// <inheritdoc />
    public ValueTask<JsonElement> GetSnapshotAsync(BridgeSnapshotContext context, CancellationToken cancellationToken) => modules.Snapshot(services, context, cancellationToken);
    /// <inheritdoc />
    public ValueTask<BridgeDispatchResult> DispatchAsync(JsonElement command, BridgeCommandContext context, CancellationToken cancellationToken) => modules.Dispatch(services, command, context, cancellationToken);
    /// <inheritdoc />
    public JsonElement ValidateError(JsonElement payload) => modules.ValidateError(payload);
}

/// <summary>Creates a logical bridge session owning its dependency injection scope.</summary>
public static class ApplicationBridgeSessionFactory
{
    /// <summary>Creates a scoped logical bridge session.</summary>
    public static ApplicationBridgeSession Create(IServiceProvider services)
    {
        AsyncServiceScope scope = services.CreateAsyncScope();
        try { return new ApplicationBridgeSession(scope.ServiceProvider.GetRequiredService<IApplicationBridgeDispatcher>(), ownedScope: scope); }
        catch { scope.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }
}
