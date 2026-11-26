using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// R7 G5, G6: what no other fixture has, for the TypeScript emitter that writes this assembly's clients from its contract
// IR. G5's dual-run gate compared the emitter with the .NET generator on these; the dual-run goldens pin the output.

// Numeric bounds that .NET writes in exponent form (1E+20, 1E-07: a double at or above 1E+17 or below 1E-04), and the
// plain neighbours on both sides of each threshold.
public sealed class DualRunBoundsViewModel : INotifyPropertyChanged
{
    [Range(0d, 1e20)]
    public double Large { get; set; }
    [Range(1e-7, 5e-5)]
    public double Small { get; set; }
    [Range(-1.5e300, 1e17)]
    public double Wide { get; set; }
    [Range(0.0001, 9e16)]
    public double Plain { get; set; }
    [Range(-0d, 1.2345678901234567e19)]
    public double NegativeZero { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Raise(string name) => PropertyChanged?.Invoke(this, new(name));
}

public sealed partial class DualRunBoundsWindow : RunicWindow<DualRunBoundsViewModel>;

// Non-ASCII names after an ASCII first letter: a View (GrößenState, pageGrößen, connectGrößenAt), members, a command and
// named types. The emitter's name scans read letters, marks, digits and connector punctuation, as .NET's \w does.
public sealed record Größe(string Maß, int Höhe);

public sealed record Grün(string Farbton);

public sealed partial class GrößenViewModel : ObservableObject
{
    [ObservableProperty] private string straße = "";
    [ObservableProperty] private Größe maße = new("", 0);
    [ObservableProperty] private Grün? farbe;
    [ObservableProperty] private double höhe;

    [RelayCommand]
    private void Löschen() => Straße = "";
}

public sealed partial class GrößenWindow : RunicWindow<GrößenViewModel>;

// #1355 (R7 G6): names that start with a non-ASCII letter: a View (ÜbersichtState, ÜbersichtClient), a member, a command
// and a named type (Änderung). The .NET generator's scans, and the emitter's until G6, read a name only from an ASCII
// letter on, so the client left these types out of its imports and re-exports and did not type-check. DualRunGoldenTests
// pins the client and type-checks it.
public sealed record Änderung(string Text, int Zähler);

public sealed partial class ÜbersichtViewModel : ObservableObject
{
    [ObservableProperty] private string ärger = "";
    [ObservableProperty] private Änderung? letzte;

    [RelayCommand]
    private void Übernehmen() => Ärger = "";
}

public sealed partial class ÜbersichtWindow : RunicWindow<ÜbersichtViewModel>;
