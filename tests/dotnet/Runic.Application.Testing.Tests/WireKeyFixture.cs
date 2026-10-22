using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests;

// #472: commands whose parameter has no wire form. KeyedItem carries a Stream, which is not a Bridge
// value, so the generator must never discover the parameter type: the client sends the resolver's key.
public sealed class KeyedItem(ulong id, string name)
{
    public ulong Id { get; } = id;
    public string Name { get; } = name;
    public bool Locked { get; set; }
    public Stream? Attachment { get; init; }
}

// The items, the resolvers' bookkeeping and the resolver failures shared by the keyed fixtures.
public sealed class KeyedItemStore
{
    public const ulong Throws = 900;
    public const ulong Rejects = 901;

    private readonly Dictionary<ulong, KeyedItem> _items = new()
    {
        [1] = new(1, "first"),
        [2] = new(2, "second") { Locked = true },
        [3] = new(3, "third"),
    };

    public IRunicModelContext? Context { get; init; }
    public int Resolutions { get; private set; }
    public List<bool> ResolvedInTurn { get; } = [];
    // While set, an asynchronous resolver waits for it, observing its token.
    public TaskCompletionSource? Gate { get; set; }
    public TaskCompletionSource ResolutionStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool ResolutionCancelled { get; private set; }

    public KeyedItem? Find(ulong id)
    {
        Resolutions++;
        if (Context is not null) ResolvedInTurn.Add(Context.IsExecuting);
        return id switch
        {
            Throws => throw new InvalidOperationException("The store is unavailable."),
            Rejects => throw new ArgumentException("The key is malformed.", nameof(id)),
            _ => _items.GetValueOrDefault(id),
        };
    }

    public async ValueTask<KeyedItem?> FindAsync(ulong id, CancellationToken cancellationToken)
    {
        var item = Find(id);
        ResolutionStarted.TrySetResult();
        if (Gate is { } gate)
        {
            try { await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                ResolutionCancelled = true;
                throw;
            }
        }
        else await Task.Yield();
        return item;
    }

    public void ResetResolutionStarted() => ResolutionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Remove(ulong id) => _items.Remove(id);
}

// ReactiveUI (ReactiveUI.Primitives flavor): sync and async resolvers, awaited commands, operations and a
// streamed result. The command's own CanExecute follows Enabled.
public sealed class KeyedReactiveViewModel : ReactiveObject
{
    private bool _enabled = true;
    private string _last = "";

    public KeyedReactiveViewModel(IRunicModelContext context)
    {
        Store = new KeyedItemStore { Context = context };
        var scheduler = ReactiveSchedulers.For(context);
        var enabled = this.WhenAnyValue(model => model.Enabled);
        DeleteCommand = ReactiveCommand.Create<KeyedItem>(item =>
        {
            if (item.Attachment is not null || item.Name.Length == 0) throw new InvalidOperationException("Unexpected item.");
            Store.Remove(item.Id);
            Last = $"deleted {item.Name}";
        }, enabled, scheduler);
        ArchiveCommand = ReactiveCommand.CreateFromTask<KeyedItem>(async item =>
        {
            await Task.Yield();
            Last = $"archived {item.Name}";
        }, enabled, scheduler);
        RenameCommand = ReactiveCommand.CreateFromTask<KeyedItem, string>(async item =>
        {
            await Task.Yield();
            return item.Name.ToUpperInvariant();
        }, enabled, scheduler);
        ExportCommand = ReactiveCommand.CreateFromObservable<KeyedItem, string>(
            item => new LettersObservable(item.Name), scheduler);
    }

    [RunicIgnore]
    public KeyedItemStore Store { get; }

    public bool Enabled
    {
        get => _enabled;
        set => this.RaiseAndSetIfChanged(ref _enabled, value);
    }

    public string Last
    {
        get => _last;
        private set => this.RaiseAndSetIfChanged(ref _last, value);
    }

    /// <summary>Deletes an item.</summary>
    [RunicCommandKey(nameof(FindItem))]
    public ReactiveCommand<KeyedItem, RxVoid> DeleteCommand { get; }

    [RunicCommandKey(nameof(FindItemAsync))]
    public ReactiveCommand<KeyedItem, RxVoid> ArchiveCommand { get; }

    [RunicCommandKey(nameof(FindItem))]
    public ReactiveCommand<KeyedItem, string> RenameCommand { get; }

    [RunicCommandKey(nameof(FindItem))]
    [RunicCommandResult(RunicCommandResultCardinality.Stream)]
    public ReactiveCommand<KeyedItem, string> ExportCommand { get; }

    public KeyedItem? FindItem(ulong itemId) => Store.Find(itemId);

    internal ValueTask<KeyedItem?> FindItemAsync(ulong itemId, CancellationToken cancellationToken) =>
        Store.FindAsync(itemId, cancellationToken);

    private sealed class LettersObservable(string text) : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer)
        {
            foreach (var letter in text) observer.OnNext(letter.ToString());
            observer.OnCompleted();
            return new Unsubscribe();
        }

        private sealed class Unsubscribe : IDisposable
        {
            public void Dispose() { }
        }
    }
}

public sealed partial class KeyedReactiveWindow : RunicWindow<KeyedReactiveViewModel>;

// CommunityToolkit: IRelayCommand<T> whose CanExecute sees the resolved item, IAsyncRelayCommand<T> with an
// asynchronous resolver, and the attribute on a [RelayCommand] method with a static resolver.
public sealed partial class KeyedToolkitViewModel : ObservableObject
{
    private string _last = "";

    public KeyedToolkitViewModel()
    {
        LockCommand = new RelayCommand<KeyedItem>(item => Last = $"locked {item!.Name}", item => item is { Locked: false });
        OpenCommand = new AsyncRelayCommand<KeyedItem>(async (item, token) =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            Last = $"opened {item!.Name}";
        }, item => item is { Locked: false });
    }

    [RunicIgnore]
    public KeyedItemStore Store { get; } = new();

    public string Last
    {
        get => _last;
        private set => SetProperty(ref _last, value);
    }

    [RunicCommandKey(nameof(FindItem))]
    public IRelayCommand<KeyedItem> LockCommand { get; }

    [RunicCommandKey(nameof(FindItemAsync))]
    public IAsyncRelayCommand<KeyedItem> OpenCommand { get; }

    [RelayCommand]
    [RunicCommandKey(nameof(Lookup))]
    private void Select(KeyedItem item) => Last = $"selected {item.Name}";

    public KeyedItem? FindItem(ulong id) => Store.Find(id);

    public Task<KeyedItem?> FindItemAsync(ulong id) => Store.FindAsync(id, CancellationToken.None).AsTask();

    // A static resolver; the key is a string.
    public static KeyedItem? Lookup(string name) => name == "fixed" ? new KeyedItem(42, "fixed") : null;
}

public sealed partial class KeyedToolkitWindow : RunicWindow<KeyedToolkitViewModel>;

// A plain ICommand takes RunicCommandKey instead of RunicCommandInput. The resolver returns a value type.
public sealed class KeyedPlainViewModel : ObservableObject
{
    public KeyedPlainViewModel() => PickCommand = new Pick(this);

    public int Picked { get; private set; }

    [RunicCommandKey(nameof(FindSlot))]
    public ICommand PickCommand { get; }

    public static int? FindSlot(int slot) => slot is >= 0 and < 4 ? slot * 10 : null;

    private sealed class Pick(KeyedPlainViewModel owner) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => parameter is int value && value != 30;
        public void Execute(object? parameter)
        {
            owner.Picked = (int)parameter!;
            owner.OnPropertyChanged(nameof(Picked));
        }
    }
}

public sealed partial class KeyedPlainWindow : RunicWindow<KeyedPlainViewModel>;
