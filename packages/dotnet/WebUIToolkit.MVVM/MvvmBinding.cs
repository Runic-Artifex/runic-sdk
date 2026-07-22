using System.Diagnostics.CodeAnalysis;

namespace WebUIToolkit.MVVM;

/// <summary>The closed set of principal member kinds in a version 1 binding vocabulary.</summary>
public enum MvvmBindingMemberKind
{
    /// <summary>A projected property, which may have a generated setter.</summary>
    Property,

    /// <summary>A projected collection.</summary>
    Collection,

    /// <summary>A projected command, which may have a generated execution handler.</summary>
    Command,
}

/// <summary>Describes one generated mutation binding.</summary>
public sealed record MvvmBindingMember
{
    /// <summary>Creates a generated binding member.</summary>
    public MvvmBindingMember(int memberId, MvvmBindingMemberKind kind, string? diagnosticName = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memberId);
        if (kind is < MvvmBindingMemberKind.Property or > MvvmBindingMemberKind.Command)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (diagnosticName is { Length: 0 })
        {
            throw new ArgumentException("A diagnostic name cannot be empty.", nameof(diagnosticName));
        }

        MemberId = memberId;
        Kind = kind;
        DiagnosticName = diagnosticName;
    }

    /// <summary>Gets the stable generated member identifier.</summary>
    public int MemberId { get; }

    /// <summary>Gets the member's principal kind.</summary>
    public MvvmBindingMemberKind Kind { get; }

    /// <summary>Gets an optional local-only diagnostic name. It is never sent by this runtime.</summary>
    public string? DiagnosticName { get; }
}

/// <summary>Handles one generated property or command mutation.</summary>
public delegate ValueTask<MvvmBindingResult> MvvmBindingHandler(
    MvvmMutationRequest request,
    CancellationToken cancellationToken);

/// <summary>Creates one authoritative projection snapshot.</summary>
public delegate ValueTask<MvvmSnapshot> MvvmBindingSnapshotHandler(CancellationToken cancellationToken);

/// <summary>Exposes the closed generated vocabulary used to validate adapter projections.</summary>
/// <remarks>
/// The official delegate adapter implements this interface. A manual adapter that does not implement
/// it remains an explicitly trusted compatibility seam whose projections cannot be kind-validated.
/// </remarks>
public interface IMvvmBindingVocabularyProvider
{
    /// <summary>Gets the complete principal-member vocabulary for the adapter.</summary>
    MvvmBindingVocabulary Vocabulary { get; }
}

/// <summary>Defines a closed, reflection-free generated binding vocabulary.</summary>
public sealed class MvvmBindingVocabulary
{
    private readonly Dictionary<int, MvvmBindingMember> _members;

    /// <summary>Creates a closed vocabulary and rejects duplicate principal member identifiers.</summary>
    public MvvmBindingVocabulary(IEnumerable<MvvmBindingMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        _members = [];
        foreach (MvvmBindingMember member in members)
        {
            ArgumentNullException.ThrowIfNull(member);
            if (_members.Count >= MvvmLimits.MaximumSnapshotMembers)
            {
                throw new ArgumentException("The vocabulary exceeds the protocol member ceiling.", nameof(members));
            }

            if (!_members.TryAdd(member.MemberId, member))
            {
                throw new ArgumentException("The vocabulary contains a duplicate principal member identifier.", nameof(members));
            }
        }

        Members = Array.AsReadOnly(_members.Values
            .OrderBy(static member => member.MemberId)
            .ToArray());
    }

    /// <summary>Gets principal members in deterministic identifier order.</summary>
    public IReadOnlyList<MvvmBindingMember> Members { get; }

    /// <summary>Finds the principal member registered for an identifier.</summary>
    public bool TryGetMember(int memberId, [NotNullWhen(true)] out MvvmBindingMember? member)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memberId);
        return _members.TryGetValue(memberId, out member);
    }

    /// <summary>Resolves a request only when both its operation and member identifier are registered.</summary>
    public bool TryResolve(MvvmMutationRequest request, [NotNullWhen(true)] out MvvmBindingMember? member)
    {
        ArgumentNullException.ThrowIfNull(request);
        MvvmBindingMemberKind kind = request.Kind switch
        {
            MvvmMutationKind.SetProperty => MvvmBindingMemberKind.Property,
            MvvmMutationKind.ExecuteCommand => MvvmBindingMemberKind.Command,
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };

        if (_members.TryGetValue(request.MemberId, out MvvmBindingMember? candidate) && candidate.Kind == kind)
        {
            member = candidate;
            return true;
        }

        member = null;
        return false;
    }
}

/// <summary>Builds a small reflection-free binding adapter from generated delegates.</summary>
public sealed class MvvmBindingAdapterBuilder
{
    private readonly MvvmBindingSnapshotHandler _snapshot;
    private readonly MvvmBindingVocabulary? _vocabulary;
    private readonly Dictionary<(MvvmMutationKind Operation, int MemberId), Registration> _registrations = [];
    private Func<ValueTask>? _dispose;
    private bool _built;

    /// <summary>Creates a builder with its authoritative snapshot delegate.</summary>
    /// <remarks>
    /// This compatibility overload infers the vocabulary from bound setters and commands. Use the
    /// vocabulary overload when the snapshot also contains read-only properties or collections.
    /// </remarks>
    public MvvmBindingAdapterBuilder(MvvmBindingSnapshotHandler snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    /// <summary>Creates a builder with its authoritative snapshot delegate and complete vocabulary.</summary>
    public MvvmBindingAdapterBuilder(
        MvvmBindingSnapshotHandler snapshot,
        MvvmBindingVocabulary vocabulary)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));
    }

    /// <summary>Registers one generated property setter.</summary>
    public MvvmBindingAdapterBuilder BindProperty(
        int memberId,
        MvvmBindingHandler handler,
        string? diagnosticName = null) =>
        Bind(new MvvmBindingMember(memberId, MvvmBindingMemberKind.Property, diagnosticName), handler);

    /// <summary>Registers one generated command.</summary>
    public MvvmBindingAdapterBuilder BindCommand(
        int memberId,
        MvvmBindingHandler handler,
        string? diagnosticName = null) =>
        Bind(new MvvmBindingMember(memberId, MvvmBindingMemberKind.Command, diagnosticName), handler);

    /// <summary>Registers asynchronous cleanup for subscriptions owned by the adapter.</summary>
    public MvvmBindingAdapterBuilder OnDispose(Func<ValueTask> dispose)
    {
        ThrowIfBuilt();
        if (_dispose is not null)
        {
            throw new InvalidOperationException("An adapter cleanup delegate is already registered.");
        }

        _dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
        return this;
    }

    /// <summary>Builds the immutable adapter. A builder can be built only once.</summary>
    public IMvvmBindingAdapter Build()
    {
        ThrowIfBuilt();
        _built = true;
        Registration[] registrations = _registrations.Values.ToArray();
        MvvmBindingVocabulary vocabulary = _vocabulary ??
            new MvvmBindingVocabulary(registrations.Select(static registration => registration.Member));
        foreach (Registration registration in registrations)
        {
            if (!vocabulary.TryGetMember(registration.Member.MemberId, out MvvmBindingMember? member) ||
                member.Kind != registration.Member.Kind)
            {
                throw new InvalidOperationException("A mutation binding does not match the supplied vocabulary.");
            }
        }

        return new DelegateBindingAdapter(_snapshot, registrations, vocabulary, _dispose);
    }

    private MvvmBindingAdapterBuilder Bind(MvvmBindingMember member, MvvmBindingHandler handler)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(handler);
        if (_registrations.Keys.Any(key => key.MemberId == member.MemberId))
        {
            throw new ArgumentException("A principal member identifier can be bound only once.", nameof(member));
        }

        MvvmMutationKind operation = member.Kind == MvvmBindingMemberKind.Property
            ? MvvmMutationKind.SetProperty
            : MvvmMutationKind.ExecuteCommand;
        if (!_registrations.TryAdd((operation, member.MemberId), new Registration(member, handler)))
        {
            throw new ArgumentException("This operation and member identifier are already bound.", nameof(member));
        }

        return this;
    }

    private void ThrowIfBuilt()
    {
        if (_built)
        {
            throw new InvalidOperationException("The binding adapter builder has already been built.");
        }
    }

    private sealed record Registration(MvvmBindingMember Member, MvvmBindingHandler Handler);

    private sealed class DelegateBindingAdapter : IMvvmBindingAdapter, IMvvmBindingVocabularyProvider
    {
        private static readonly MvvmFault UnknownMemberFault =
            new(MvvmFaultCodes.MemberUnknown, "The requested member is unknown.");

        private readonly MvvmBindingSnapshotHandler _snapshot;
        private readonly Dictionary<(MvvmMutationKind Operation, int MemberId), Registration> _registrations;
        private readonly Func<ValueTask>? _dispose;
        private readonly object _disposeGate = new();
        private Task? _disposeTask;

        internal DelegateBindingAdapter(
            MvvmBindingSnapshotHandler snapshot,
            Registration[] registrations,
            MvvmBindingVocabulary vocabulary,
            Func<ValueTask>? dispose)
        {
            _snapshot = snapshot;
            _registrations = registrations.ToDictionary(
                static registration =>
                    (registration.Member.Kind == MvvmBindingMemberKind.Property
                        ? MvvmMutationKind.SetProperty
                        : MvvmMutationKind.ExecuteCommand,
                    registration.Member.MemberId));
            _dispose = dispose;
            Vocabulary = vocabulary;
        }

        public MvvmBindingVocabulary Vocabulary { get; }

        public ValueTask<MvvmSnapshot> SnapshotAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return _snapshot(cancellationToken);
        }

        public ValueTask<MvvmBindingResult> DispatchAsync(
            MvvmMutationRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ThrowIfDisposed();
            return _registrations.TryGetValue((request.Kind, request.MemberId), out Registration? registration)
                ? registration.Handler(request, cancellationToken)
                : ValueTask.FromResult(MvvmBindingResult.Rejected(UnknownMemberFault));
        }

        public ValueTask DisposeAsync()
        {
            TaskCompletionSource? completion = null;
            Task disposeTask;
            lock (_disposeGate)
            {
                if (_disposeTask is null)
                {
                    completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _disposeTask = completion.Task;
                }

                disposeTask = _disposeTask;
            }

            if (completion is not null)
            {
                _ = DisposeCoreAsync(completion);
            }

            return new ValueTask(disposeTask);
        }

        private async Task DisposeCoreAsync(TaskCompletionSource completion)
        {
            try
            {
                if (_dispose is not null)
                {
                    await _dispose().ConfigureAwait(false);
                }

                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private void ThrowIfDisposed()
        {
            lock (_disposeGate)
            {
                ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            }
        }
    }
}
