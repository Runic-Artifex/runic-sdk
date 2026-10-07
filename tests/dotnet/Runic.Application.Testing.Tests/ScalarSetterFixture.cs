using System.ComponentModel;
using System.Runtime.CompilerServices;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

public enum ScalarMode { Plain, [RunicAlias("fancy")] Fancy }

// One settable property per argument encoding of a generated setter.
public sealed class ScalarSetterViewModel : INotifyPropertyChanged
{
    private int _count;
    private bool _enabled;
    private string _name = "";
    private string? _note = "note";
    private long _big;
    private int? _maybe = 1;
    private bool? _flag = true;
    private double _ratio;
    private decimal _price;
    private Guid _id;
    private ScalarMode _mode;

    public int Count { get => _count; set => Set(ref _count, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string? Note { get => _note; set => Set(ref _note, value); }
    public long Big { get => _big; set => Set(ref _big, value); }
    public int? Maybe { get => _maybe; set => Set(ref _maybe, value); }
    public bool? Flag { get => _flag; set => Set(ref _flag, value); }
    public double Ratio { get => _ratio; set => Set(ref _ratio, value); }
    public decimal Price { get => _price; set => Set(ref _price, value); }
    public Guid Id { get => _id; set => Set(ref _id, value); }
    public ScalarMode Mode { get => _mode; set => Set(ref _mode, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}

public sealed partial class ScalarSetterWindow(ScalarSetterViewModel model) : RunicWindow<ScalarSetterViewModel>(model);
