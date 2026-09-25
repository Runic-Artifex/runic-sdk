using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

public sealed partial class RootWindow(RootViewModel model) : RunicWindow<RootViewModel>(model);

public sealed partial class ChildView : RunicView<ChildViewModel>,
    IRunicViewLifetime, IRunicWebMountLifetime, IDisposable
{
    public static int Attached { get; private set; }
    public static int Detached { get; private set; }
    public static int Mounted { get; private set; }
    public static int Unmounted { get; private set; }
    public static int Disposed { get; private set; }

    public void OnAttached() => Attached++;
    public void OnDetached() => Detached++;
    public void OnWebMounted() => Mounted++;
    public void OnWebUnmounted() => Unmounted++;
    public void Dispose() => Disposed++;
}

public sealed class RootViewModel : INotifyPropertyChanged
{
    private int _count;
    private ChildViewModel? _child = new();

    public int Count
    {
        get => _count;
        set { _count = value; PropertyChanged?.Invoke(this, new(nameof(Count))); }
    }

    public ChildViewModel? Child
    {
        get => _child;
        private set { _child = value; PropertyChanged?.Invoke(this, new(nameof(Child))); }
    }

    public void ClearChild() => Child = null;

    public IRelayCommand IncrementCommand { get; }

    public RootViewModel() => IncrementCommand = new RelayCommand(() => Count++);

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ChildViewModel : INotifyPropertyChanged
{
    private string _title = "initial";

    public string Title
    {
        get => _title;
        set { _title = value; PropertyChanged?.Invoke(this, new(nameof(Title))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class TestViewLocator : IRunicViewLocator
{
    public TView Locate<TView, TViewModel>() where TView : class, IRunicView where TViewModel : class =>
        typeof(TView) == typeof(ChildView)
            ? (TView)(object)new ChildView()
            : throw new InvalidOperationException($"Unexpected View {typeof(TView).Name}.");
}
