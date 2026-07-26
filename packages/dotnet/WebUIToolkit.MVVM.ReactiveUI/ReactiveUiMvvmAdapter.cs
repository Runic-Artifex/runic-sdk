using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using WebUIToolkit.MVVM;

namespace WebUIToolkit.MVVM.ReactiveUI;

/// <summary>Describes one explicitly bound ReactiveUI member.</summary>
public sealed record ReactiveUiBindingMetadata
{
    /// <summary>Creates deterministic metadata for a direct-access ReactiveUI binding.</summary>
    public ReactiveUiBindingMetadata(int memberId, MvvmBindingMemberKind kind, string generatedMemberName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memberId);
        if (kind is < MvvmBindingMemberKind.Property or > MvvmBindingMemberKind.Command)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrEmpty(generatedMemberName);
        MemberId = memberId;
        Kind = kind;
        GeneratedMemberName = generatedMemberName;
    }

    /// <summary>Gets the stable protocol member identifier.</summary>
    public int MemberId { get; }

    /// <summary>Gets the closed protocol member kind.</summary>
    public MvvmBindingMemberKind Kind { get; }

    /// <summary>Gets the compile-time generated property or command name.</summary>
    public string GeneratedMemberName { get; }
}

/// <summary>Builds a closed ReactiveUI adapter over one concrete ViewModel.</summary>
public sealed class ReactiveUiMvvmAdapterBuilder<TViewModel>
    where TViewModel : class
{
    private readonly TViewModel _viewModel;
    private readonly List<ReactiveUiMvvmBindingAdapter<TViewModel>.IReactiveBinding<TViewModel>> _bindings = [];
    private IScheduler _scheduler = ImmediateScheduler.Instance;
    private Func<TViewModel, IDisposable>? _activate;
    private Action<Exception>? _faultHandler;
    private bool _built;

    /// <summary>Creates a builder over an explicitly supplied ViewModel.</summary>
    public ReactiveUiMvvmAdapterBuilder(TViewModel viewModel) =>
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

    /// <summary>Observes command state and faults on the supplied scheduler.</summary>
    public ReactiveUiMvvmAdapterBuilder<TViewModel> ObserveOn(IScheduler scheduler)
    {
        ThrowIfBuilt();
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        return this;
    }

    /// <summary>Scopes one ReactiveUI activation lease to the adapter lifetime.</summary>
    public ReactiveUiMvvmAdapterBuilder<TViewModel> ActivateWith(Func<TViewModel, IDisposable> activate)
    {
        ThrowIfBuilt();
        _activate = activate ?? throw new ArgumentNullException(nameof(activate));
        return this;
    }

    /// <summary>Routes every observed command fault to an application-owned bounded sink.</summary>
    public ReactiveUiMvvmAdapterBuilder<TViewModel> RouteFaultsTo(Action<Exception> faultHandler)
    {
        ThrowIfBuilt();
        _faultHandler = faultHandler ?? throw new ArgumentNullException(nameof(faultHandler));
        return this;
    }

    /// <summary>Adds a generated reactive property with closed serializer metadata.</summary>
    public ReactiveUiMvvmAdapterBuilder<TViewModel> BindProperty<TValue>(
        int memberId,
        string generatedPropertyName,
        Func<TViewModel, TValue> get,
        Action<TViewModel, TValue> set,
        JsonTypeInfo<TValue> jsonTypeInfo)
    {
        ThrowIfBuilt();
        Add(new ReactiveUiMvvmBindingAdapter<TViewModel>.PropertyBinding<TViewModel, TValue>(
            new ReactiveUiBindingMetadata(memberId, MvvmBindingMemberKind.Property, generatedPropertyName),
            get ?? throw new ArgumentNullException(nameof(get)),
            set ?? throw new ArgumentNullException(nameof(set)),
            jsonTypeInfo ?? throw new ArgumentNullException(nameof(jsonTypeInfo))));
        return this;
    }

    /// <summary>Adds an observable collection with closed item serializer metadata.</summary>
    public ReactiveUiMvvmAdapterBuilder<TViewModel> BindCollection<TItem>(
        int memberId,
        string generatedPropertyName,
        Func<TViewModel, IReadOnlyList<TItem>> get,
        JsonTypeInfo<TItem> itemJsonTypeInfo)
    {
        ThrowIfBuilt();
        Add(new ReactiveUiMvvmBindingAdapter<TViewModel>.CollectionBinding<TViewModel, TItem>(
            new ReactiveUiBindingMetadata(memberId, MvvmBindingMemberKind.Collection, generatedPropertyName),
            get ?? throw new ArgumentNullException(nameof(get)),
            itemJsonTypeInfo ?? throw new ArgumentNullException(nameof(itemJsonTypeInfo))));
        return this;
    }

    /// <summary>Adds a typed ReactiveCommand whose one output becomes the protocol result payload.</summary>
    public ReactiveUiMvvmAdapterBuilder<TViewModel> BindCommand<TInput, TOutput>(
        int memberId,
        string generatedCommandName,
        Func<TViewModel, ReactiveCommand<TInput, TOutput>> get,
        JsonTypeInfo<TInput> inputJsonTypeInfo,
        JsonTypeInfo<TOutput> outputJsonTypeInfo)
    {
        ThrowIfBuilt();
        Add(new ReactiveUiMvvmBindingAdapter<TViewModel>.CommandBinding<TViewModel, TInput, TOutput>(
            new ReactiveUiBindingMetadata(memberId, MvvmBindingMemberKind.Command, generatedCommandName),
            get ?? throw new ArgumentNullException(nameof(get)),
            inputJsonTypeInfo ?? throw new ArgumentNullException(nameof(inputJsonTypeInfo)),
            outputJsonTypeInfo ?? throw new ArgumentNullException(nameof(outputJsonTypeInfo))));
        return this;
    }

    /// <summary>Adds a parameterless ReactiveCommand with a typed output.</summary>
    public ReactiveUiMvvmAdapterBuilder<TViewModel> BindCommand<TOutput>(
        int memberId,
        string generatedCommandName,
        Func<TViewModel, ReactiveCommand<Unit, TOutput>> get,
        JsonTypeInfo<TOutput> outputJsonTypeInfo)
    {
        ThrowIfBuilt();
        Add(new ReactiveUiMvvmBindingAdapter<TViewModel>.UnitCommandBinding<TViewModel, TOutput>(
            new ReactiveUiBindingMetadata(memberId, MvvmBindingMemberKind.Command, generatedCommandName),
            get ?? throw new ArgumentNullException(nameof(get)),
            outputJsonTypeInfo ?? throw new ArgumentNullException(nameof(outputJsonTypeInfo))));
        return this;
    }

    /// <summary>Builds the adapter and acquires its activation/subscription scope.</summary>
    public ReactiveUiMvvmBindingAdapter<TViewModel> Build()
    {
        ThrowIfBuilt();
        _built = true;
        return new ReactiveUiMvvmBindingAdapter<TViewModel>(
            _viewModel,
            _bindings,
            _scheduler,
            _activate,
            _faultHandler);
    }

    private void Add(ReactiveUiMvvmBindingAdapter<TViewModel>.IReactiveBinding<TViewModel> binding)
    {
        if (_bindings.Any(existing => existing.Metadata.MemberId == binding.Metadata.MemberId))
        {
            throw new ArgumentException("A principal member identifier can be bound only once.", nameof(binding));
        }

        _bindings.Add(binding);
    }

    private void ThrowIfBuilt()
    {
        if (_built)
        {
            throw new InvalidOperationException("The ReactiveUI adapter builder has already been built.");
        }
    }
}

/// <summary>Closed ReactiveUI binding adapter with deterministic activation and Rx disposal.</summary>
public sealed class ReactiveUiMvvmBindingAdapter<TViewModel> :
    IMvvmBindingAdapter,
    IMvvmBindingVocabularyProvider
    where TViewModel : class
{
    private static readonly MvvmFault UnknownMember =
        new(MvvmFaultCodes.MemberUnknown, "The requested member is unknown.");
    private static readonly MvvmFault InvalidRequest =
        new(MvvmFaultCodes.RequestInvalid, "The requested value is invalid.");
    private static readonly MvvmFault CommandFault =
        new(MvvmFaultCodes.RequestInvalid, "The reactive command failed.");

    private readonly TViewModel _viewModel;
    private readonly IReactiveBinding<TViewModel>[] _bindings;
    private readonly Dictionary<int, IReactiveBinding<TViewModel>> _byMemberId;
    private readonly CompositeDisposable _lifetime = [];
    private readonly Action<Exception>? _faultHandler;
    private readonly object _gate = new();
    private bool _disposed;

    internal ReactiveUiMvvmBindingAdapter(
        TViewModel viewModel,
        IEnumerable<IReactiveBinding<TViewModel>> bindings,
        IScheduler scheduler,
        Func<TViewModel, IDisposable>? activate,
        Action<Exception>? faultHandler)
    {
        _viewModel = viewModel;
        _bindings = bindings.OrderBy(static binding => binding.Metadata.MemberId).ToArray();
        _byMemberId = _bindings.ToDictionary(static binding => binding.Metadata.MemberId);
        _faultHandler = faultHandler;
        Metadata = Array.AsReadOnly(_bindings.Select(static binding => binding.Metadata).ToArray());
        Vocabulary = new MvvmBindingVocabulary(_bindings.Select(static binding => new MvvmBindingMember(
            binding.Metadata.MemberId,
            binding.Metadata.Kind,
            binding.Metadata.GeneratedMemberName)));

        if (activate is not null)
        {
            _lifetime.Add(activate(viewModel));
        }

        if (viewModel is INotifyPropertyChanged notify)
        {
            PropertyChangedEventHandler handler = (_, _) => { };
            notify.PropertyChanged += handler;
            _lifetime.Add(Disposable.Create(() => notify.PropertyChanged -= handler));
        }

        foreach (IReactiveBinding<TViewModel> binding in _bindings)
        {
            _lifetime.Add(binding.Subscribe(viewModel, scheduler, RouteFault));
        }
    }

    /// <summary>Gets deterministic binding metadata ordered by member identifier.</summary>
    public IReadOnlyList<ReactiveUiBindingMetadata> Metadata { get; }

    /// <inheritdoc />
    public MvvmBindingVocabulary Vocabulary { get; }

    /// <summary>Gets the number of adapter-owned activation and subscription leases.</summary>
    public int OwnedLeaseCount => _lifetime.Count;

    /// <inheritdoc />
    public ValueTask<MvvmSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CreateSnapshot());
    }

    /// <inheritdoc />
    public async ValueTask<MvvmBindingResult> DispatchAsync(
        MvvmMutationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_byMemberId.TryGetValue(request.MemberId, out IReactiveBinding<TViewModel>? binding) ||
            binding.Operation != request.Kind)
        {
            return MvvmBindingResult.Rejected(UnknownMember);
        }

        try
        {
            MvvmBindingResult result = await binding.DispatchAsync(
                _viewModel,
                request.Payload,
                Vocabulary,
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded || request.Kind != MvvmMutationKind.ExecuteCommand)
            {
                return result;
            }

            var patches = new MvvmProjectionPatchBuilder(Vocabulary);
            foreach (MvvmPatch patch in result.Patches)
            {
                patches.Add(patch);
            }

            foreach (IReactiveBinding<TViewModel> candidate in _bindings)
            {
                if (candidate.Metadata.Kind == MvvmBindingMemberKind.Collection)
                {
                    candidate.AddPatch(_viewModel, patches);
                }
            }

            return MvvmBindingResult.Success(result.Payload, patches.Build());
        }
        catch (JsonException)
        {
            return MvvmBindingResult.Rejected(InvalidRequest);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RouteFault(exception);
            return MvvmBindingResult.CommittedFailure(CommandFault, CreatePatches());
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
        }

        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }

    private MvvmSnapshot CreateSnapshot()
    {
        var builder = new MvvmProjectionSnapshotBuilder(Vocabulary);
        foreach (IReactiveBinding<TViewModel> binding in _bindings)
        {
            binding.AddSnapshot(_viewModel, builder);
        }

        return builder.Build();
    }

    private IReadOnlyList<MvvmPatch> CreatePatches()
    {
        var builder = new MvvmProjectionPatchBuilder(Vocabulary);
        foreach (IReactiveBinding<TViewModel> binding in _bindings)
        {
            binding.AddPatch(_viewModel, builder);
        }

        return builder.Build();
    }

    private void RouteFault(Exception exception)
    {
        try
        {
            _faultHandler?.Invoke(exception);
        }
        catch
        {
            // An application fault sink may not escape into protocol dispatch.
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    internal interface IReactiveBinding<in TModel>
    {
        ReactiveUiBindingMetadata Metadata { get; }
        MvvmMutationKind? Operation { get; }
        void AddSnapshot(TModel viewModel, MvvmProjectionSnapshotBuilder builder);
        void AddPatch(TModel viewModel, MvvmProjectionPatchBuilder builder);
        IDisposable Subscribe(TModel viewModel, IScheduler scheduler, Action<Exception> faultHandler);
        ValueTask<MvvmBindingResult> DispatchAsync(
            TModel viewModel,
            JsonElement payload,
            MvvmBindingVocabulary vocabulary,
            CancellationToken cancellationToken);
    }

    internal sealed class PropertyBinding<TModel, TValue> : IReactiveBinding<TModel>
    {
        private readonly Func<TModel, TValue> _get;
        private readonly Action<TModel, TValue> _set;
        private readonly JsonTypeInfo<TValue> _jsonTypeInfo;

        internal PropertyBinding(
            ReactiveUiBindingMetadata metadata,
            Func<TModel, TValue> get,
            Action<TModel, TValue> set,
            JsonTypeInfo<TValue> jsonTypeInfo)
        {
            Metadata = metadata;
            _get = get;
            _set = set;
            _jsonTypeInfo = jsonTypeInfo;
        }

        public ReactiveUiBindingMetadata Metadata { get; }
        public MvvmMutationKind? Operation => MvvmMutationKind.SetProperty;

        public void AddSnapshot(TModel viewModel, MvvmProjectionSnapshotBuilder builder) =>
            builder.AddProperty(Metadata.MemberId, JsonSerializer.SerializeToElement(_get(viewModel), _jsonTypeInfo));

        public void AddPatch(TModel viewModel, MvvmProjectionPatchBuilder builder) =>
            builder.Property(Metadata.MemberId, JsonSerializer.SerializeToElement(_get(viewModel), _jsonTypeInfo));

        public IDisposable Subscribe(TModel viewModel, IScheduler scheduler, Action<Exception> faultHandler) =>
            Disposable.Empty;

        public ValueTask<MvvmBindingResult> DispatchAsync(
            TModel viewModel,
            JsonElement payload,
            MvvmBindingVocabulary vocabulary,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TValue? value = JsonSerializer.Deserialize(payload, _jsonTypeInfo);
            _set(viewModel, value!);
            var patches = new MvvmProjectionPatchBuilder(vocabulary);
            AddPatch(viewModel, patches);
            return ValueTask.FromResult(patches.Success());
        }
    }

    internal sealed class CollectionBinding<TModel, TItem> : IReactiveBinding<TModel>
    {
        private readonly Func<TModel, IReadOnlyList<TItem>> _get;
        private readonly JsonTypeInfo<TItem> _itemJsonTypeInfo;

        internal CollectionBinding(
            ReactiveUiBindingMetadata metadata,
            Func<TModel, IReadOnlyList<TItem>> get,
            JsonTypeInfo<TItem> itemJsonTypeInfo)
        {
            Metadata = metadata;
            _get = get;
            _itemJsonTypeInfo = itemJsonTypeInfo;
        }

        public ReactiveUiBindingMetadata Metadata { get; }
        public MvvmMutationKind? Operation => null;

        public void AddSnapshot(TModel viewModel, MvvmProjectionSnapshotBuilder builder) =>
            builder.AddCollection(Metadata.MemberId, SerializeItems(_get(viewModel)));

        public void AddPatch(TModel viewModel, MvvmProjectionPatchBuilder builder) =>
            builder.Collection(
                Metadata.MemberId,
                MvvmCollectionOperation.Reset,
                index: 0,
                SerializeItems(_get(viewModel)));

        public IDisposable Subscribe(
            TModel viewModel,
            IScheduler scheduler,
            Action<Exception> faultHandler)
        {
            if (_get(viewModel) is not INotifyCollectionChanged notifyingCollection)
            {
                return Disposable.Empty;
            }

            NotifyCollectionChangedEventHandler handler = static (_, _) => { };
            notifyingCollection.CollectionChanged += handler;
            return Disposable.Create(() => notifyingCollection.CollectionChanged -= handler);
        }

        public ValueTask<MvvmBindingResult> DispatchAsync(
            TModel viewModel,
            JsonElement payload,
            MvvmBindingVocabulary vocabulary,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MvvmBindingResult.Rejected(UnknownMember));

        private JsonElement[] SerializeItems(IReadOnlyList<TItem> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            if (items.Count > MvvmLimits.MaximumCollectionItems)
            {
                throw new InvalidOperationException(
                    "The projected collection exceeds the protocol item ceiling.");
            }

            var serialized = new JsonElement[items.Count];
            for (int index = 0; index < items.Count; index++)
            {
                serialized[index] = JsonSerializer.SerializeToElement(
                    items[index],
                    _itemJsonTypeInfo);
            }

            return serialized;
        }
    }

    internal abstract class CommandBindingBase<TModel> : IReactiveBinding<TModel>
    {
        private readonly object _stateGate = new();
        private bool _canExecute;
        private bool _isExecuting;

        protected CommandBindingBase(ReactiveUiBindingMetadata metadata) => Metadata = metadata;

        public ReactiveUiBindingMetadata Metadata { get; }
        public MvvmMutationKind? Operation => MvvmMutationKind.ExecuteCommand;

        protected bool CanExecute
        {
            get { lock (_stateGate) return _canExecute; }
            private set { lock (_stateGate) _canExecute = value; }
        }

        protected bool IsExecuting
        {
            get { lock (_stateGate) return _isExecuting; }
            private set { lock (_stateGate) _isExecuting = value; }
        }

        public void AddSnapshot(TModel viewModel, MvvmProjectionSnapshotBuilder builder) =>
            builder.AddCommand(Metadata.MemberId, CanExecute, IsExecuting);

        public void AddPatch(TModel viewModel, MvvmProjectionPatchBuilder builder) =>
            builder.Command(Metadata.MemberId, CanExecute, IsExecuting);

        public IDisposable Subscribe(TModel viewModel, IScheduler scheduler, Action<Exception> faultHandler)
        {
            IReactiveCommand command = GetCommand(viewModel);
            var subscriptions = new CompositeDisposable
            {
                command.CanExecute.ObserveOn(scheduler).Subscribe(
                    value => CanExecute = value,
                    faultHandler),
                command.IsExecuting.ObserveOn(scheduler).Subscribe(
                    value => IsExecuting = value,
                    faultHandler),
                command.ThrownExceptions.ObserveOn(scheduler).Subscribe(
                    faultHandler,
                    faultHandler),
            };
            return subscriptions;
        }

        public abstract ValueTask<MvvmBindingResult> DispatchAsync(
            TModel viewModel,
            JsonElement payload,
            MvvmBindingVocabulary vocabulary,
            CancellationToken cancellationToken);

        protected abstract IReactiveCommand GetCommand(TModel viewModel);
    }

    internal sealed class CommandBinding<TModel, TInput, TOutput> : CommandBindingBase<TModel>
    {
        private readonly Func<TModel, ReactiveCommand<TInput, TOutput>> _get;
        private readonly JsonTypeInfo<TInput> _inputJsonTypeInfo;
        private readonly JsonTypeInfo<TOutput> _outputJsonTypeInfo;

        internal CommandBinding(
            ReactiveUiBindingMetadata metadata,
            Func<TModel, ReactiveCommand<TInput, TOutput>> get,
            JsonTypeInfo<TInput> inputJsonTypeInfo,
            JsonTypeInfo<TOutput> outputJsonTypeInfo)
            : base(metadata)
        {
            _get = get;
            _inputJsonTypeInfo = inputJsonTypeInfo;
            _outputJsonTypeInfo = outputJsonTypeInfo;
        }

        protected override IReactiveCommand GetCommand(TModel viewModel) => _get(viewModel);

        public override async ValueTask<MvvmBindingResult> DispatchAsync(
            TModel viewModel,
            JsonElement payload,
            MvvmBindingVocabulary vocabulary,
            CancellationToken cancellationToken)
        {
            if (!CanExecute)
            {
                return MvvmBindingResult.Rejected(InvalidRequest);
            }

            TInput? input = JsonSerializer.Deserialize(payload, _inputJsonTypeInfo);
            TOutput output = await AwaitSingleAsync(_get(viewModel).Execute(input!), cancellationToken)
                .ConfigureAwait(false);
            JsonElement result = JsonSerializer.SerializeToElement(output, _outputJsonTypeInfo);
            var patches = new MvvmProjectionPatchBuilder(vocabulary);
            AddPatch(viewModel, patches);
            return patches.Success(result);
        }
    }

    internal sealed class UnitCommandBinding<TModel, TOutput> : CommandBindingBase<TModel>
    {
        private readonly Func<TModel, ReactiveCommand<Unit, TOutput>> _get;
        private readonly JsonTypeInfo<TOutput> _outputJsonTypeInfo;

        internal UnitCommandBinding(
            ReactiveUiBindingMetadata metadata,
            Func<TModel, ReactiveCommand<Unit, TOutput>> get,
            JsonTypeInfo<TOutput> outputJsonTypeInfo)
            : base(metadata)
        {
            _get = get;
            _outputJsonTypeInfo = outputJsonTypeInfo;
        }

        protected override IReactiveCommand GetCommand(TModel viewModel) => _get(viewModel);

        public override async ValueTask<MvvmBindingResult> DispatchAsync(
            TModel viewModel,
            JsonElement payload,
            MvvmBindingVocabulary vocabulary,
            CancellationToken cancellationToken)
        {
            if (!CanExecute || payload.ValueKind is not JsonValueKind.Null)
            {
                return MvvmBindingResult.Rejected(InvalidRequest);
            }

            TOutput output = await AwaitSingleAsync(_get(viewModel).Execute(), cancellationToken)
                .ConfigureAwait(false);
            JsonElement result = JsonSerializer.SerializeToElement(output, _outputJsonTypeInfo);
            var patches = new MvvmProjectionPatchBuilder(vocabulary);
            AddPatch(viewModel, patches);
            return patches.Success(result);
        }
    }

    private static Task<T> AwaitSingleAsync<T>(IObservable<T> source, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = new SingleAssignmentDisposable();
        T? last = default;
        bool hasValue = false;
        subscription.Disposable = source.Subscribe(
            value =>
            {
                last = value;
                hasValue = true;
            },
            exception => completion.TrySetException(exception),
            () =>
            {
                if (hasValue)
                {
                    completion.TrySetResult(last!);
                }
                else
                {
                    completion.TrySetException(new InvalidOperationException(
                        "The reactive command completed without one result."));
                }
            });
        CancellationTokenRegistration cancellation = cancellationToken.Register(() =>
        {
            subscription.Dispose();
            completion.TrySetCanceled(cancellationToken);
        });
        _ = completion.Task.ContinueWith(
            _ =>
            {
                cancellation.Dispose();
                subscription.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return completion.Task;
    }
}
