using System.Runtime.CompilerServices;
using System.Text.Json;
using ReactiveUI.Binding;
using Runic.Application.Views;

namespace Runic.Application.Views.ReactiveUI;

/// <summary>
/// Factory used by generated ReactiveUI bridges to expose one typed interaction
/// to a mounted browser endpoint. It deliberately registers one ReactiveUI
/// handler per Interaction object, regardless of how many views or windows
/// present that object.
/// </summary>
public static class ReactiveInteractionDescriptor
{
    public static BridgeInteractionDescriptor<TModel> Create<TModel, TInput, TOutput>(
        string name,
        string contract,
        Func<TModel, IInteraction<TInput, TOutput>> getInteraction,
        Func<TInput, string> encodeInput,
        Func<JsonElement, TOutput> decodeOutput,
        TimeSpan? timeout = null)
        where TModel : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(contract);
        ArgumentNullException.ThrowIfNull(getInteraction);
        ArgumentNullException.ThrowIfNull(encodeInput);
        ArgumentNullException.ThrowIfNull(decodeOutput);
        if (timeout is { } value && (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(10)))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Interaction timeouts must be between zero and ten minutes.");
        return new Descriptor<TModel, TInput, TOutput>(name, contract, getInteraction, encodeInput, decodeOutput, timeout);
    }

    private sealed class Descriptor<TModel, TInput, TOutput>(string name, string contract,
        Func<TModel, IInteraction<TInput, TOutput>> getInteraction,
        Func<TInput, string> encodeInput, Func<JsonElement, TOutput> decodeOutput,
        TimeSpan? timeout) : BridgeInteractionDescriptor<TModel> where TModel : class
    {
        public override IDisposable Attach(WindowContentSession session, TModel model, string route)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(model);
            ArgumentException.ThrowIfNullOrWhiteSpace(route);
            var interaction = getInteraction(model) ?? throw new InvalidOperationException(
                $"The interaction '{name}' resolved to null.");
            var routeLease = session.Interactions.Register(route, name, contract, encodeInput, decodeOutput,
                timeout: timeout);
            try
            {
                var handlerLease = SharedHandlers<TInput, TOutput>.Attach(interaction,
                    new Binding<TInput, TOutput>(session, route, name, contract, encodeInput, decodeOutput, timeout));
                return new CompositeLease(routeLease, handlerLease);
            }
            catch
            {
                routeLease.Dispose();
                throw;
            }
        }
    }

    private sealed record Binding<TInput, TOutput>(WindowContentSession Session, string Route, string Name,
        string Contract, Func<TInput, string> EncodeInput, Func<JsonElement, TOutput> DecodeOutput, TimeSpan? Timeout);

    private static class SharedHandlers<TInput, TOutput>
    {
        private static readonly ConditionalWeakTable<IInteraction<TInput, TOutput>, Handler> Handlers = new();

        public static IDisposable Attach(IInteraction<TInput, TOutput> interaction,
            Binding<TInput, TOutput> binding) =>
            Handlers.GetValue(interaction, static key => new Handler(key)).Attach(binding);

        private sealed class Handler(IInteraction<TInput, TOutput> interaction)
        {
            private readonly object _gate = new();
            private readonly List<Binding<TInput, TOutput>> _bindings = [];
            private IDisposable? _registration;

            public IDisposable Attach(Binding<TInput, TOutput> binding)
            {
                lock (_gate)
                {
                    var conflicting = _bindings.FirstOrDefault(existing => ReferenceEquals(existing.Session, binding.Session)
                        && string.Equals(existing.Route, binding.Route, StringComparison.Ordinal)
                        && (!string.Equals(existing.Name, binding.Name, StringComparison.Ordinal)
                            || !string.Equals(existing.Contract, binding.Contract, StringComparison.Ordinal)));
                    if (conflicting is not null)
                        throw new InvalidOperationException("The same ReactiveUI Interaction instance cannot be exposed by multiple generated interaction members on one bridge route.");
                    if (_registration is null)
                        _registration = interaction.RegisterHandler(HandleAsync);
                    _bindings.Add(binding);
                }
                return new HandlerLease(this, binding);
            }

            private async Task HandleAsync(IInteractionContext<TInput, TOutput> context)
            {
                var invocation = RunicInteractionInvocation.Current;
                if (invocation is null) return;
                Binding<TInput, TOutput>? binding;
                lock (_gate)
                    binding = _bindings.FirstOrDefault(candidate => ReferenceEquals(candidate.Session, invocation.Session)
                        && string.Equals(candidate.Route, invocation.Route, StringComparison.Ordinal));
                if (binding is null) return;

                var request = binding.Session.Interactions.TryRequest(binding.Route, binding.Name, binding.Contract,
                    context.Input, binding.EncodeInput, binding.DecodeOutput, binding.Timeout);
                if (request is null) return;
                context.SetOutput(await request.ConfigureAwait(false));
            }

            private void Remove(Binding<TInput, TOutput> binding)
            {
                lock (_gate)
                {
                    _bindings.Remove(binding);
                    if (_bindings.Count != 0 || _registration is null) return;
                    _registration.Dispose();
                    _registration = null;
                }
            }

            private sealed class HandlerLease(Handler owner, Binding<TInput, TOutput> binding) : IDisposable
            {
                private Handler? _owner = owner;
                public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Remove(binding);
            }
        }
    }

    private sealed class CompositeLease(IDisposable first, IDisposable second) : IDisposable
    {
        private IDisposable? _first = first;
        private IDisposable? _second = second;
        public void Dispose()
        {
            var second = Interlocked.Exchange(ref _second, null);
            var first = Interlocked.Exchange(ref _first, null);
            try { second?.Dispose(); }
            finally { first?.Dispose(); }
        }
    }
}
