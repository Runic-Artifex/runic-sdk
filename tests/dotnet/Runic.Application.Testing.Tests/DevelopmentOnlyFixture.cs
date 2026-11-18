using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// D072.12/D072.13 (#1109 request 3): development-only views and members ([RunicDevelopmentOnly]), which a trimmed or
// NativeAOT publish leaves out (DevelopmentOnlyTests).

/// <summary>A ViewModel whose development tools a publish leaves out.</summary>
public sealed partial class DevelopmentToolsViewModel : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = "";

    /// <summary>A diagnostics note.</summary>
    [ObservableProperty, RunicDevelopmentOnly] public partial string Debug { get; set; } = "debug";

    [RunicDevelopmentOnly, RunicCollection(nameof(DevelopmentTrace.Id))]
    public ObservableCollection<DevelopmentTrace> Traces { get; } = [new(1, "start")];

    public int Resets { get; private set; }

    [RelayCommand]
    private void Rename(string title) => Title = title;

    /// <summary>Resets the diagnostics.</summary>
    [RelayCommand, RunicDevelopmentOnly]
    private void Reset() => Resets++;

    [RelayCommand, RunicDevelopmentOnly]
    private Task<int> InjectAsync(string fault) => Task.FromResult(fault.Length + Resets);
}

public sealed record DevelopmentTrace(int Id, string Text);

public sealed partial class DevelopmentToolsWindow : RunicWindow<DevelopmentToolsViewModel>;

/// <summary>A mock-control panel that only development needs.</summary>
[RunicDevelopmentOnly]
public sealed partial class DevelopmentPanelViewModel : ObservableObject
{
    [ObservableProperty] public partial string Scenario { get; set; } = "";
}

public sealed partial class DevelopmentPanelWindow : RunicWindow<DevelopmentPanelViewModel>;

// DevelopmentToolsViewModel again: a Bridge reads its descriptor table once per ViewModel type, so the test of a publish
// that leaves the members out needs a type whose table no other test read with them.
public sealed partial class LeftOutToolsViewModel : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = "";

    [ObservableProperty, RunicDevelopmentOnly] public partial string Debug { get; set; } = "debug";

    [RunicDevelopmentOnly, RunicCollection(nameof(DevelopmentTrace.Id))]
    public ObservableCollection<DevelopmentTrace> Traces { get; } = [new(1, "start")];

    public int Resets { get; private set; }

    [RelayCommand]
    private void Rename(string title) => Title = title;

    [RelayCommand, RunicDevelopmentOnly]
    private void Reset() => Resets++;

    [RelayCommand, RunicDevelopmentOnly]
    private Task<int> InjectAsync(string fault) => Task.FromResult(fault.Length + Resets);
}

public sealed partial class LeftOutToolsWindow : RunicWindow<LeftOutToolsViewModel>;
