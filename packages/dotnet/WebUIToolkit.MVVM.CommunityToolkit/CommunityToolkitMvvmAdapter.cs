using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using WebUIToolkit.MVVM;

namespace WebUIToolkit.MVVM.CommunityToolkit;

/// <summary>Describes the declared CommunityToolkit member behind one closed MVVM binding member.</summary>
public sealed record CommunityToolkitBindingMetadata
{
    /// <summary>Creates deterministic metadata for one explicitly generated member.</summary>
    public CommunityToolkitBindingMetadata(int memberId, MvvmBindingMemberKind kind, string generatedMemberName)
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

    /// <summary>Gets the member's principal protocol kind.</summary>
    public MvvmBindingMemberKind Kind { get; }

    /// <summary>Gets the generated public property or command name.</summary>
    public string GeneratedMemberName { get; }
}

/// <summary>Builds a closed, direct-access adapter for one CommunityToolkit-generated ViewModel surface.</summary>
/// <typeparam name="TViewModel">The concrete attributed partial ViewModel type.</typeparam>
/// <remarks>
/// Calls to this builder are expected to be emitted from compile-time symbols. The builder deliberately
/// accepts direct delegates and source-generated JSON metadata rather than resolving members at runtime.
/// </remarks>
public sealed class CommunityToolkitMvvmAdapterBuilder<TViewModel>
    where TViewModel : class
{
    private readonly TViewModel _viewModel;
    private readonly List<CommunityToolkitMvvmBindingAdapter<TViewModel>.ICommunityToolkitBinding<TViewModel>> _bindings = [];
    private bool _built;

    /// <summary>Creates a builder over one explicitly supplied ViewModel instance.</summary>
    public CommunityToolkitMvvmAdapterBuilder(TViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    /// <summary>Adds a compiler-generated observable property reference.</summary>
    public CommunityToolkitMvvmAdapterBuilder<TViewModel> BindProperty<TValue>(
        MvvmPropertyReference property,
        Func<TViewModel, TValue> get,
        Action<TViewModel, TValue> set,
        JsonTypeInfo<TValue> jsonTypeInfo,
        bool includeValidation = false) =>
        BindProperty(
            property.MemberId,
            property.GeneratedMemberName,
            get,
            set,
            jsonTypeInfo,
            includeValidation);

    /// <summary>Adds a compiler-generated observable property using a source-generated JSON context.</summary>
    public CommunityToolkitMvvmAdapterBuilder<TViewModel> BindProperty<TValue>(
        MvvmPropertyReference property,
        Func<TViewModel, TValue> get,
        Action<TViewModel, TValue> set,
        JsonSerializerContext jsonContext,
        bool includeValidation = false) =>
        BindProperty(
            property,
            get,
            set,
            RequireJsonTypeInfo<TValue>(jsonContext),
            includeValidation);

    /// <summary>Adds a generated observable property with a closed JSON representation.</summary>
    /// <typeparam name="TValue">The property's closed declared type.</typeparam>
    public CommunityToolkitMvvmAdapterBuilder<TViewModel> BindProperty<TValue>(
        int memberId,
        string generatedPropertyName,
        Func<TViewModel, TValue> get,
        Action<TViewModel, TValue> set,
        JsonTypeInfo<TValue> jsonTypeInfo,
        bool includeValidation = false)
    {
        ThrowIfBuilt();
        ArgumentException.ThrowIfNullOrEmpty(generatedPropertyName);
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        Add(new CommunityToolkitMvvmBindingAdapter<TViewModel>.PropertyBinding<TViewModel, TValue>(
            new CommunityToolkitBindingMetadata(memberId, MvvmBindingMemberKind.Property, generatedPropertyName),
            get,
            set,
            jsonTypeInfo,
            includeValidation));
        return this;
    }

    /// <summary>Adds a generated read-only or derived property with a closed JSON representation.</summary>
    public CommunityToolkitMvvmAdapterBuilder<TViewModel> BindReadOnlyProperty<TValue>(
        int memberId,
        string generatedPropertyName,
        Func<TViewModel, TValue> get,
        JsonTypeInfo<TValue> jsonTypeInfo,
        bool includeValidation = false)
    {
        ThrowIfBuilt();
        ArgumentException.ThrowIfNullOrEmpty(generatedPropertyName);
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        Add(new CommunityToolkitMvvmBindingAdapter<TViewModel>.PropertyBinding<TViewModel, TValue>(
            new CommunityToolkitBindingMetadata(memberId, MvvmBindingMemberKind.Property, generatedPropertyName),
            get,
            set: null,
            jsonTypeInfo,
            includeValidation));
        return this;
    }

    /// <summary>Adds a compiler-generated read-only property reference.</summary>
    public CommunityToolkitMvvmAdapterBuilder<TViewModel> BindReadOnlyProperty<TValue>(
        MvvmPropertyReference property,
        Func<TViewModel, TValue> get,
        JsonTypeInfo<TValue> jsonTypeInfo,
        bool includeValidation = false) =>
        BindReadOnlyProperty(
            property.MemberId,
            property.GeneratedMemberName,
            get,
            jsonTypeInfo,
            includeValidation);

    /// <summary>Adds a compiler-generated read-only property using a source-generated JSON context.</summary>
    public CommunityToolkitMvvmAdapterBuilder<TViewModel> BindReadOnlyProperty<TValue>(
        MvvmPropertyReference property,
        Func<TViewModel, TValue> get,
        JsonSerializerContext jsonContext,
        bool includeValidation = false) =>
        BindReadOnlyProperty(
            property,
            get,
            RequireJsonTypeInfo<TValue>(jsonContext),
            includeValidation);

    /// <summary>Adds a compiler-generated collection reference.</summary>
    public CommunityToolkitMvvmAdapterBuilder<TViewModel> BindCollection<TItem>(
        MvvmCollectionReference collection,
        Func<TViewModel, IReadOnlyList<TItem>> get,
        JsonTypeInfo<TItem> itemJsonTypeInfo,
        bool includeValidation = false) =>
        BindCollection(
            collection.MemberId,
            collection.GeneratedMemberName,
            get,
            itemJsonTypeInfo,
            includeValidation);

    /// <summary>Adds a compiler-generated collection using a source-generated JSON context.</summary>
    public CommunityToolkitMvvmAdapterBuilder<TViewModel> BindCollection<TItem>(
        MvvmCollectionReference collection,
        Func<TViewModel, IReadOnlyList<TItem>> get,
        JsonSerializerContext jsonContext,
        bool includeValidation = false) =>
        BindCollection(
            collection,
            get,
            RequireJsonTypeInfo<TItem>(jsonContext),
            includeValidation);

    /// <summary>Adds a generated observable collection with a closed item representation.</summary>
    /// <typeparam name="TItem">The collection item's closed declared type.</typeparam>
    public CommunityToolkitMvvmAdapterBuilder<TViewModel> BindCollection<TItem>(
        int memberId,
        string generatedPropertyName,
        Func<TViewModel, IReadOnlyList<TItem>> get,
        JsonTypeInfo<TItem> itemJsonTypeInfo,
        bool includeValidation = false)
    {
        ThrowIfBuilt();
        ArgumentException.ThrowIfNullOrEmpty(generatedPropertyName);
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(itemJsonTypeInfo);
        Add(new CommunityToolkitMvvmBindingAdapter<TViewModel>.CollectionBinding<TViewModel, TItem>(
            new CommunityToolkitBindingMetadata(memberId, MvvmBindingMemberKind.Collection, generatedPropertyName),
            get,
            itemJsonTypeInfo,
            includeValidation));
        return this;
    }

    /// <summary>Adds a parameterless generated relay command.</summary>
    public CommunityToolkitMvvmAdapterBuilder<TViewModel> BindCommand(
        int memberId,
        string generatedCommandName,
        Func<TViewModel, IRelayCommand> get)
    {
        ThrowIfBuilt();
        ArgumentException.ThrowIfNullOrEmpty(generatedCommandName);
        ArgumentNullException.ThrowIfNull(get);
        Add(new CommunityToolkitMvvmBindingAdapter<TViewModel>.CommandBinding<TViewModel>(
            new CommunityToolkitBindingMetadata(memberId, MvvmBindingMemberKind.Command, generatedCommandName),
            get,
            null,
            null));
        return this;
    }

    /// <summary>Adds a compiler-generated parameterless relay-command reference.</summary>
    public CommunityToolkitMvvmAdapterBuilder<TViewModel> BindCommand(
        MvvmCommandReference command,
        Func<TViewModel, IRelayCommand> get) =>
        BindCommand(command.MemberId, command.GeneratedMemberName, get);

    /// <summary>Adds a compiler-generated parameterless asynchronous command through command inference.</summary>
    public CommunityToolkitMvvmAdapterBuilder<TViewModel> BindCommand(
        MvvmCommandReference command,
        Func<TViewModel, IAsyncRelayCommand> get) =>
        BindAsyncCommand(command.MemberId, command.GeneratedMemberName, get);

    /// <summary>Adds a generated relay command with a closed typed parameter.</summary>
    /// <typeparam name="TParameter">The command's declared parameter type.</typeparam>
    public CommunityToolkitMvvmAdapterBuilder<TViewModel> BindCommand<TParameter>(
        int memberId,
        string generatedCommandName,
        Func<TViewModel, IRelayCommand> get,
        JsonTypeInfo<TParameter> parameterJsonTypeInfo)
    {
        ThrowIfBuilt();
        ArgumentException.ThrowIfNullOrEmpty(generatedCommandName);
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(parameterJsonTypeInfo);
        Add(new CommunityToolkitMvvmBindingAdapter<TViewModel>.CommandBinding<TViewModel>(
            new CommunityToolkitBindingMetadata(memberId, MvvmBindingMemberKind.Command, generatedCommandName),
            get,
            static (payload, metadata) => JsonSerializer.Deserialize<TParameter>(payload, (JsonTypeInfo<TParameter>)metadata!),
            parameterJsonTypeInfo));
        return this;
    }

    /// <summary>Adds a compiler-generated typed relay-command reference.</summary>
    public CommunityToolkitMvvmAdapterBuilder<TViewModel> BindCommand<TParameter>(
        MvvmCommandReference command,
        Func<TViewModel, IRelayCommand> get,
        JsonTypeInfo<TParameter> parameterJsonTypeInfo) =>
        BindCommand(
            command.MemberId,
            command.GeneratedMemberName,
            get,
            parameterJsonTypeInfo);

    /// <summary>Adds a parameterless generated asynchronous relay command.</summary>
    public CommunityToolkitMvvmAdapterBuilder<TViewModel> BindAsyncCommand(
        int memberId,
        string generatedCommandName,
        Func<TViewModel, IAsyncRelayCommand> get)
    {
        ThrowIfBuilt();
        ArgumentException.ThrowIfNullOrEmpty(generatedCommandName);
        ArgumentNullException.ThrowIfNull(get);
        Add(new CommunityToolkitMvvmBindingAdapter<TViewModel>.AsyncCommandBinding<TViewModel>(
            new CommunityToolkitBindingMetadata(memberId, MvvmBindingMemberKind.Command, generatedCommandName),
            get,
            null,
            null));
        return this;
    }

    /// <summary>Adds a compiler-generated parameterless asynchronous-command reference.</summary>
    public CommunityToolkitMvvmAdapterBuilder<TViewModel> BindAsyncCommand(
        MvvmCommandReference command,
        Func<TViewModel, IAsyncRelayCommand> get) =>
        BindAsyncCommand(command.MemberId, command.GeneratedMemberName, get);

    /// <summary>Adds a generated asynchronous relay command with a closed typed parameter.</summary>
    /// <typeparam name="TParameter">The command's declared parameter type.</typeparam>
    public CommunityToolkitMvvmAdapterBuilder<TViewModel> BindAsyncCommand<TParameter>(
        int memberId,
        string generatedCommandName,
        Func<TViewModel, IAsyncRelayCommand> get,
        JsonTypeInfo<TParameter> parameterJsonTypeInfo)
    {
        ThrowIfBuilt();
        ArgumentException.ThrowIfNullOrEmpty(generatedCommandName);
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(parameterJsonTypeInfo);
        Add(new CommunityToolkitMvvmBindingAdapter<TViewModel>.AsyncCommandBinding<TViewModel>(
            new CommunityToolkitBindingMetadata(memberId, MvvmBindingMemberKind.Command, generatedCommandName),
            get,
            static (payload, metadata) => JsonSerializer.Deserialize<TParameter>(payload, (JsonTypeInfo<TParameter>)metadata!),
            parameterJsonTypeInfo));
        return this;
    }

    /// <summary>Adds a compiler-generated typed asynchronous-command reference.</summary>
    public CommunityToolkitMvvmAdapterBuilder<TViewModel> BindAsyncCommand<TParameter>(
        MvvmCommandReference command,
        Func<TViewModel, IAsyncRelayCommand> get,
        JsonTypeInfo<TParameter> parameterJsonTypeInfo) =>
        BindAsyncCommand(
            command.MemberId,
            command.GeneratedMemberName,
            get,
            parameterJsonTypeInfo);

    /// <summary>Builds the immutable adapter and subscribes to the declared CommunityToolkit event surface.</summary>
    public CommunityToolkitMvvmBindingAdapter<TViewModel> Build()
    {
        ThrowIfBuilt();
        _built = true;
        return new CommunityToolkitMvvmBindingAdapter<TViewModel>(_viewModel, _bindings);
    }

    private void Add(CommunityToolkitMvvmBindingAdapter<TViewModel>.ICommunityToolkitBinding<TViewModel> binding)
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
            throw new InvalidOperationException("The binding adapter builder has already been built.");
        }
    }

    private static JsonTypeInfo<TValue> RequireJsonTypeInfo<TValue>(
        JsonSerializerContext jsonContext)
    {
        ArgumentNullException.ThrowIfNull(jsonContext);
        return jsonContext.GetTypeInfo(typeof(TValue)) as JsonTypeInfo<TValue> ??
            throw new InvalidOperationException(
                $"The source-generated JSON context does not contain '{typeof(TValue)}'.");
    }
}

/// <summary>A closed CommunityToolkit adapter with direct member access and deterministic subscription disposal.</summary>
/// <typeparam name="TViewModel">The concrete attributed partial ViewModel type.</typeparam>
public sealed class CommunityToolkitMvvmBindingAdapter<TViewModel> :
    IMvvmBindingAdapter,
    IMvvmBindingVocabularyProvider,
    IMvvmBindingChangeSource
    where TViewModel : class
{
    private static readonly MvvmFault UnknownMember = new(MvvmFaultCodes.MemberUnknown, "The requested member is unknown.");
    private static readonly MvvmFault InvalidRequest = new(MvvmFaultCodes.RequestInvalid, "The requested value is invalid.");
    private readonly TViewModel _viewModel;
    private readonly IReadOnlyList<ICommunityToolkitBinding<TViewModel>> _bindings;
    private readonly Dictionary<int, ICommunityToolkitBinding<TViewModel>> _byMemberId;
    private readonly List<Action> _unsubscribe = [];
    private readonly object _disposeGate = new();
    private int _dispatchDepth;
    private bool _disposed;

    internal CommunityToolkitMvvmBindingAdapter(
        TViewModel viewModel,
        IEnumerable<ICommunityToolkitBinding<TViewModel>> bindings)
    {
        _viewModel = viewModel;
        _bindings = bindings.OrderBy(static binding => binding.Metadata.MemberId).ToArray();
        _byMemberId = _bindings.ToDictionary(static binding => binding.Metadata.MemberId);
        Metadata = Array.AsReadOnly(_bindings.Select(static binding => binding.Metadata).ToArray());
        Vocabulary = new MvvmBindingVocabulary(_bindings.Select(static binding => new MvvmBindingMember(
            binding.Metadata.MemberId,
            binding.Metadata.Kind,
            binding.Metadata.GeneratedMemberName)));
        Subscribe();
    }

    /// <summary>Gets deterministic metadata ordered by protocol member identifier.</summary>
    public IReadOnlyList<CommunityToolkitBindingMetadata> Metadata { get; }

    /// <inheritdoc />
    public MvvmBindingVocabulary Vocabulary { get; }

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public ValueTask<MvvmSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new MvvmProjectionSnapshotBuilder(Vocabulary);
        foreach (ICommunityToolkitBinding<TViewModel> binding in _bindings)
        {
            binding.AddSnapshot(_viewModel, snapshot);
        }

        return ValueTask.FromResult(snapshot.Build());
    }

    /// <inheritdoc />
    public async ValueTask<MvvmBindingResult> DispatchAsync(MvvmMutationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_byMemberId.TryGetValue(request.MemberId, out ICommunityToolkitBinding<TViewModel>? binding) ||
            !binding.Accepts(request.Kind))
        {
            return MvvmBindingResult.Rejected(UnknownMember);
        }

        Interlocked.Increment(ref _dispatchDepth);
        MvvmBindingResult result;
        try
        {
            result = await binding
                .DispatchAsync(_viewModel, request.Payload, Vocabulary, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _dispatchDepth);
        }
        if (!result.Succeeded)
        {
            return result;
        }

        // CommunityToolkit notifications are synchronous. Re-project authoritative state in
        // identifier order so property/validation changes made by a command, and relay availability
        // changes made by a property mutation, commit in the same deterministic transaction.
        var patches = new MvvmProjectionPatchBuilder(Vocabulary);
        bool includePropertyState = request.Kind == MvvmMutationKind.ExecuteCommand;
        if (!includePropertyState)
        {
            foreach (MvvmPatch patch in result.Patches)
            {
                patches.Add(patch);
            }
        }

        foreach (ICommunityToolkitBinding<TViewModel> candidate in _bindings)
        {
            candidate.AddPostDispatchPatches(_viewModel, patches, includePropertyState);
        }

        return MvvmBindingResult.Success(result.Payload, patches.Build());
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<MvvmPatch>> ProjectChangesAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var patches = new MvvmProjectionPatchBuilder(Vocabulary);
        foreach (ICommunityToolkitBinding<TViewModel> binding in _bindings)
        {
            binding.AddPostDispatchPatches(_viewModel, patches, includePropertyState: true);
        }

        return ValueTask.FromResult(patches.Build());
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            foreach (Action unsubscribe in _unsubscribe)
            {
                unsubscribe();
            }

            _unsubscribe.Clear();
        }

        return ValueTask.CompletedTask;
    }

    private void Subscribe()
    {
        if (_viewModel is INotifyPropertyChanged notifyingViewModel)
        {
            PropertyChangedEventHandler handler = OnViewModelPropertyChanged;
            notifyingViewModel.PropertyChanged += handler;
            _unsubscribe.Add(() => notifyingViewModel.PropertyChanged -= handler);
        }

        if (_viewModel is INotifyDataErrorInfo errors)
        {
            EventHandler<DataErrorsChangedEventArgs> handler = OnErrorsChanged;
            errors.ErrorsChanged += handler;
            _unsubscribe.Add(() => errors.ErrorsChanged -= handler);
        }

        foreach (ICommunityToolkitBinding<TViewModel> binding in _bindings)
        {
            binding.Subscribe(_viewModel, _unsubscribe, NotifyStateChanged);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        NotifyStateChanged();
    }

    private void OnErrorsChanged(object? sender, DataErrorsChangedEventArgs e)
    {
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        if (Volatile.Read(ref _dispatchDepth) != 0 || _disposed)
        {
            return;
        }

        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Session observers cannot affect ViewModel notifications.
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_disposeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    internal interface ICommunityToolkitBinding<T>
        where T : class
    {
        CommunityToolkitBindingMetadata Metadata { get; }

        bool Accepts(MvvmMutationKind kind);

        void AddSnapshot(T viewModel, MvvmProjectionSnapshotBuilder snapshot);

        ValueTask<MvvmBindingResult> DispatchAsync(
            T viewModel,
            JsonElement payload,
            MvvmBindingVocabulary vocabulary,
            CancellationToken cancellationToken);

        void Subscribe(T viewModel, List<Action> unsubscribe, Action stateChanged);

        void AddPostDispatchPatches(
            T viewModel,
            MvvmProjectionPatchBuilder patches,
            bool includePropertyState);
    }

    internal sealed class PropertyBinding<T, TValue> : ICommunityToolkitBinding<T>
        where T : class
    {
        private readonly Func<T, TValue> _get;
        private readonly Action<T, TValue>? _set;
        private readonly JsonTypeInfo<TValue> _jsonTypeInfo;
        private readonly bool _includeValidation;

        public PropertyBinding(
            CommunityToolkitBindingMetadata metadata,
            Func<T, TValue> get,
            Action<T, TValue>? set,
            JsonTypeInfo<TValue> jsonTypeInfo,
            bool includeValidation)
        {
            Metadata = metadata;
            _get = get;
            _set = set;
            _jsonTypeInfo = jsonTypeInfo;
            _includeValidation = includeValidation;
        }

        public CommunityToolkitBindingMetadata Metadata { get; }

        public bool Accepts(MvvmMutationKind kind) =>
            kind == MvvmMutationKind.SetProperty && _set is not null;

        public void AddSnapshot(T viewModel, MvvmProjectionSnapshotBuilder snapshot)
        {
            snapshot.AddProperty(Metadata.MemberId, MvvmValue.From(_get(viewModel), _jsonTypeInfo));
            AddValidation(viewModel, snapshot);
        }

        public ValueTask<MvvmBindingResult> DispatchAsync(
            T viewModel,
            JsonElement payload,
            MvvmBindingVocabulary vocabulary,
            CancellationToken cancellationToken)
        {
            try
            {
                TValue value = JsonSerializer.Deserialize<TValue>(payload, _jsonTypeInfo)!;
                cancellationToken.ThrowIfCancellationRequested();
                _set!(viewModel, value);
                cancellationToken.ThrowIfCancellationRequested();
                var patches = new MvvmProjectionPatchBuilder(vocabulary)
                    .Property(Metadata.MemberId, MvvmValue.From(_get(viewModel), _jsonTypeInfo));
                AddValidation(viewModel, patches);
                return ValueTask.FromResult(patches.Success());
            }
            catch (JsonException)
            {
                return ValueTask.FromResult(MvvmBindingResult.Rejected(InvalidRequest));
            }
        }

        public void Subscribe(T viewModel, List<Action> unsubscribe, Action stateChanged)
        {
        }

        public void AddPostDispatchPatches(
            T viewModel,
            MvvmProjectionPatchBuilder patches,
            bool includePropertyState)
        {
            if (!includePropertyState)
            {
                return;
            }

            patches.Property(Metadata.MemberId, MvvmValue.From(_get(viewModel), _jsonTypeInfo));
            AddValidation(viewModel, patches);
        }

        private void AddValidation(T viewModel, MvvmProjectionSnapshotBuilder snapshot)
        {
            if (_includeValidation && viewModel is INotifyDataErrorInfo errors)
            {
                snapshot.AddValidation(Metadata.MemberId, ReadErrors(errors, Metadata.GeneratedMemberName));
            }
        }

        private void AddValidation(T viewModel, MvvmProjectionPatchBuilder patches)
        {
            if (_includeValidation && viewModel is INotifyDataErrorInfo errors)
            {
                patches.Validation(Metadata.MemberId, ReadErrors(errors, Metadata.GeneratedMemberName));
            }
        }
    }

    internal sealed class CollectionBinding<T, TItem> : ICommunityToolkitBinding<T>
        where T : class
    {
        private readonly Func<T, IReadOnlyList<TItem>> _get;
        private readonly JsonTypeInfo<TItem> _itemJsonTypeInfo;
        private readonly bool _includeValidation;

        public CollectionBinding(
            CommunityToolkitBindingMetadata metadata,
            Func<T, IReadOnlyList<TItem>> get,
            JsonTypeInfo<TItem> itemJsonTypeInfo,
            bool includeValidation)
        {
            Metadata = metadata;
            _get = get;
            _itemJsonTypeInfo = itemJsonTypeInfo;
            _includeValidation = includeValidation;
        }

        public CommunityToolkitBindingMetadata Metadata { get; }

        public bool Accepts(MvvmMutationKind kind) => false;

        public void AddSnapshot(T viewModel, MvvmProjectionSnapshotBuilder snapshot)
        {
            snapshot.AddCollection(Metadata.MemberId, SerializeItems(_get(viewModel)));
            AddValidation(viewModel, snapshot);
        }

        public ValueTask<MvvmBindingResult> DispatchAsync(
            T viewModel,
            JsonElement payload,
            MvvmBindingVocabulary vocabulary,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MvvmBindingResult.Rejected(UnknownMember));

        public void Subscribe(T viewModel, List<Action> unsubscribe, Action stateChanged)
        {
            if (_get(viewModel) is INotifyCollectionChanged notifyingCollection)
            {
                NotifyCollectionChangedEventHandler handler = (_, _) => stateChanged();
                notifyingCollection.CollectionChanged += handler;
                unsubscribe.Add(() => notifyingCollection.CollectionChanged -= handler);
            }
        }

        public void AddPostDispatchPatches(
            T viewModel,
            MvvmProjectionPatchBuilder patches,
            bool includePropertyState)
        {
            if (!includePropertyState)
            {
                return;
            }

            patches.Collection(
                Metadata.MemberId,
                MvvmCollectionOperation.Reset,
                index: 0,
                SerializeItems(_get(viewModel)));
            AddValidation(viewModel, patches);
        }

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
                serialized[index] = MvvmValue.From(items[index], _itemJsonTypeInfo);
            }

            return serialized;
        }

        private void AddValidation(T viewModel, MvvmProjectionSnapshotBuilder snapshot)
        {
            if (_includeValidation && viewModel is INotifyDataErrorInfo errors)
            {
                snapshot.AddValidation(Metadata.MemberId, ReadErrors(errors, Metadata.GeneratedMemberName));
            }
        }

        private void AddValidation(T viewModel, MvvmProjectionPatchBuilder patches)
        {
            if (_includeValidation && viewModel is INotifyDataErrorInfo errors)
            {
                patches.Validation(Metadata.MemberId, ReadErrors(errors, Metadata.GeneratedMemberName));
            }
        }

    }

    internal sealed class CommandBinding<T> : ICommunityToolkitBinding<T>
        where T : class
    {
        private readonly Func<T, IRelayCommand> _get;
        private readonly Func<JsonElement, object?, object?>? _deserialize;
        private readonly object? _jsonTypeInfo;

        public CommandBinding(
            CommunityToolkitBindingMetadata metadata,
            Func<T, IRelayCommand> get,
            Func<JsonElement, object?, object?>? deserialize,
            object? jsonTypeInfo)
        {
            Metadata = metadata;
            _get = get;
            _deserialize = deserialize;
            _jsonTypeInfo = jsonTypeInfo;
        }

        public CommunityToolkitBindingMetadata Metadata { get; }

        public bool Accepts(MvvmMutationKind kind) => kind == MvvmMutationKind.ExecuteCommand;

        public void AddSnapshot(T viewModel, MvvmProjectionSnapshotBuilder snapshot)
        {
            IRelayCommand command = _get(viewModel);
            snapshot.AddCommand(Metadata.MemberId, command.CanExecute(null), isExecuting: false);
        }

        public ValueTask<MvvmBindingResult> DispatchAsync(
            T viewModel,
            JsonElement payload,
            MvvmBindingVocabulary vocabulary,
            CancellationToken cancellationToken)
        {
            try
            {
                object? parameter = _deserialize is null ? null : _deserialize(payload, _jsonTypeInfo);
                IRelayCommand command = _get(viewModel);
                cancellationToken.ThrowIfCancellationRequested();
                if (!command.CanExecute(parameter))
                {
                    return ValueTask.FromResult(MvvmBindingResult.Rejected(InvalidRequest));
                }

                command.Execute(parameter);
                return ValueTask.FromResult(new MvvmProjectionPatchBuilder(vocabulary)
                    .Command(Metadata.MemberId, command.CanExecute(parameter), isExecuting: false)
                    .Success());
            }
            catch (JsonException)
            {
                return ValueTask.FromResult(MvvmBindingResult.Rejected(InvalidRequest));
            }
        }

        public void Subscribe(T viewModel, List<Action> unsubscribe, Action stateChanged)
        {
            IRelayCommand command = _get(viewModel);
            EventHandler handler = (_, _) => stateChanged();
            command.CanExecuteChanged += handler;
            unsubscribe.Add(() => command.CanExecuteChanged -= handler);
        }

        public void AddPostDispatchPatches(
            T viewModel,
            MvvmProjectionPatchBuilder patches,
            bool includePropertyState)
        {
            IRelayCommand command = _get(viewModel);
            patches.Command(Metadata.MemberId, command.CanExecute(null), isExecuting: false);
        }
    }

    internal sealed class AsyncCommandBinding<T> : ICommunityToolkitBinding<T>
        where T : class
    {
        private readonly Func<T, IAsyncRelayCommand> _get;
        private readonly Func<JsonElement, object?, object?>? _deserialize;
        private readonly object? _jsonTypeInfo;

        public AsyncCommandBinding(
            CommunityToolkitBindingMetadata metadata,
            Func<T, IAsyncRelayCommand> get,
            Func<JsonElement, object?, object?>? deserialize,
            object? jsonTypeInfo)
        {
            Metadata = metadata;
            _get = get;
            _deserialize = deserialize;
            _jsonTypeInfo = jsonTypeInfo;
        }

        public CommunityToolkitBindingMetadata Metadata { get; }

        public bool Accepts(MvvmMutationKind kind) => kind == MvvmMutationKind.ExecuteCommand;

        public void AddSnapshot(T viewModel, MvvmProjectionSnapshotBuilder snapshot)
        {
            IAsyncRelayCommand command = _get(viewModel);
            snapshot.AddCommand(Metadata.MemberId, command.CanExecute(null), command.IsRunning);
        }

        public async ValueTask<MvvmBindingResult> DispatchAsync(
            T viewModel,
            JsonElement payload,
            MvvmBindingVocabulary vocabulary,
            CancellationToken cancellationToken)
        {
            object? parameter;
            try
            {
                parameter = _deserialize is null ? null : _deserialize(payload, _jsonTypeInfo);
            }
            catch (JsonException)
            {
                return MvvmBindingResult.Rejected(InvalidRequest);
            }

            IAsyncRelayCommand command = _get(viewModel);
            cancellationToken.ThrowIfCancellationRequested();
            if (!command.CanExecute(parameter))
            {
                return MvvmBindingResult.Rejected(InvalidRequest);
            }

            using CancellationTokenRegistration cancellation = cancellationToken.Register(static state =>
                ((IAsyncRelayCommand)state!).Cancel(), command);
            command.Execute(parameter);
            Task? execution = command.ExecutionTask;
            if (execution is not null)
            {
                // Once cancellation has been forwarded to the Toolkit command, wait for its terminal
                // task instead of returning a cancellation while its ViewModel can still mutate.
                await execution.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new MvvmProjectionPatchBuilder(vocabulary)
                .Command(Metadata.MemberId, command.CanExecute(parameter), command.IsRunning)
                .Success();
        }

        public void Subscribe(T viewModel, List<Action> unsubscribe, Action stateChanged)
        {
            IAsyncRelayCommand command = _get(viewModel);
            EventHandler canExecuteHandler = (_, _) => stateChanged();
            command.CanExecuteChanged += canExecuteHandler;
            unsubscribe.Add(() => command.CanExecuteChanged -= canExecuteHandler);
            if (command is INotifyPropertyChanged notifyingCommand)
            {
                PropertyChangedEventHandler propertyChangedHandler = (_, _) => stateChanged();
                notifyingCommand.PropertyChanged += propertyChangedHandler;
                unsubscribe.Add(() => notifyingCommand.PropertyChanged -= propertyChangedHandler);
            }
        }

        public void AddPostDispatchPatches(
            T viewModel,
            MvvmProjectionPatchBuilder patches,
            bool includePropertyState)
        {
            IAsyncRelayCommand command = _get(viewModel);
            patches.Command(Metadata.MemberId, command.CanExecute(null), command.IsRunning);
        }
    }

    private static List<string> ReadErrors(INotifyDataErrorInfo errors, string propertyName)
    {
        System.Collections.IEnumerable values = errors.GetErrors(propertyName) ?? Array.Empty<object>();
        var result = new List<string>();
        foreach (object? value in values)
        {
            if (value is not null)
            {
                result.Add(value.ToString() ?? string.Empty);
            }
        }

        return result;
    }
}
