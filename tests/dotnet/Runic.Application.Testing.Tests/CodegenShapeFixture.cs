using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// Each member is a shape whose generated C# previously failed to compile or
// lost data: enum aliases, ImmutableArray, nullable structs, ReadOnlyCollection,
// nullable array elements and mutable list interfaces.
public enum CodegenShapeMode
{
    None = 0,
    Default = 0,
    Active = 1,
}

public readonly record struct CodegenPoint(int X, int Y);

public sealed class CodegenShapeViewModel : INotifyPropertyChanged
{
    public CodegenShapeMode Mode { get; set; } = CodegenShapeMode.Default;
    public ImmutableArray<int> Numbers { get; set; } = [1, 2];
    public ImmutableArray<string?>? OptionalNames { get; set; }
    public CodegenPoint? Origin { get; set; }
    public CodegenPoint Corner { get; set; } = new(3, 4);
    public ReadOnlyCollection<string> Tags { get; set; } = new(["first"]);
    public string?[] Labels { get; set; } = ["label", null];
    public IList<string> Editable { get; set; } = new List<string> { "edit" };
    public DateTimeOffset Stamp { get; set; } = new(2026, 10, 3, 12, 30, 0, TimeSpan.FromHours(2));
    public DateTime When { get; set; } = new(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc);
    public TimeOnly At { get; set; } = new(9, 30);

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Raise(string name) => PropertyChanged?.Invoke(this, new(name));
}

public sealed partial class CodegenShapeWindow(CodegenShapeViewModel model) : RunicWindow<CodegenShapeViewModel>(model);
