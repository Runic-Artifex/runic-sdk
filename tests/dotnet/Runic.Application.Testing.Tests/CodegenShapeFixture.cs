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
#pragma warning disable CA1069 // The duplicate value is the enum-alias shape under test.
    Default = 0,
#pragma warning restore CA1069
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

// Nullable ReactiveUI command and interaction type arguments must survive
// into the generated codecs and TypeScript client.
public sealed class NullableReactiveViewModel : ReactiveUI.ReactiveObject, IDisposable
{
    public NullableReactiveViewModel() => EchoCommand = ReactiveUI.ReactiveCommand.Create<string?, string?>(value => value);

    public ReactiveUI.ReactiveCommand<string?, string?> EchoCommand { get; }
    public ReactiveUI.Binding.Interaction<string, string?> AskName { get; } = new();
    public string Label { get; } = "nullable";

    public void Dispose() => EchoCommand.Dispose();
}

public sealed partial class NullableReactiveWindow(NullableReactiveViewModel model) : RunicWindow<NullableReactiveViewModel>(model);

/// <summary>How the documented item is shown.</summary>
public enum DocumentedLayout
{
    /// <summary>One item per row.</summary>
    List,

    /// <summary>Items in a grid.</summary>
    Grid,
}

/// <summary>A documented item.</summary>
/// <param name="Title">The item's <c>title</c>.</param>
public sealed record DocumentedItem(string Title);

/// <summary>
/// Generated TypeScript names its enum and DTO types after the C# types,
/// copies these XML comments and uses the command method's parameter name.
/// </summary>
public sealed partial class DocumentedViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private DocumentedLayout _layout;

    /// <summary>The current layout.</summary>
    public DocumentedLayout Layout { get => _layout; private set => SetProperty(ref _layout, value); }

    public DocumentedItem Item { get; } = new("first");

    /// <summary>The note being edited.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _note = "";

    // Also used by CodegenShapeViewModel: named types are declared once.
    public CodegenPoint Corner { get; } = new(1, 2);

    /// <summary>Switches to <paramref name="layout"/>.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ChangeLayout(DocumentedLayout layout) => Layout = layout;
}

public sealed partial class DocumentedWindow(DocumentedViewModel model) : RunicWindow<DocumentedViewModel>(model);
