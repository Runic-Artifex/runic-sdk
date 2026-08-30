using System;
using System.Threading.Tasks;

namespace Runic.CommandLine.Testing;

/// <summary>Creates deterministic scopes backed by a supplied service provider.</summary>
public sealed class TestExecutionScopeFactory : ICommandExecutionScopeFactory
{
    private readonly IServiceProvider _services;

    /// <summary>Initializes the factory.</summary>
    public TestExecutionScopeFactory(IServiceProvider services) => _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <inheritdoc />
    public ICommandExecutionScope CreateScope() => new Scope(_services);

    private sealed class Scope : ICommandExecutionScope
    {
        public Scope(IServiceProvider services) => Services = services;
        public IServiceProvider Services { get; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
