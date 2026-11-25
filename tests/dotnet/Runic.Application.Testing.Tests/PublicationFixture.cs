using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// #1320: a ViewModel ported from WPF in explicit publication (PublicationTests). It publishes its [RunicInclude] members
// and the member of its [RunicSharedClient] interface; the members its native view bound to stay on .NET.

/// <summary>The summary every import result page shows.</summary>
[RunicSharedClient]
public interface IImportSummary
{
    /// <summary>One line about the import.</summary>
    string Summary { get; }
}

[RunicPublication(RunicPublicationMode.Explicit)]
public sealed partial class ImportResultViewModel : ObservableObject, IImportSummary
{
    [ObservableProperty, RunicInclude] public partial string Title { get; set; } = "Import";

    public string Summary => $"{Imported.Count} imported, {Skipped.Count} skipped";

    // Kept for the native view and the tests; the page reads none of them.
    public List<string> Imported { get; } = ["a", "b"];
    public List<string> Skipped { get; } = ["c"];
    [ObservableProperty] public partial bool ShowSkipped { get; set; }

    public int Retries { get; private set; }
    public int HelpShown { get; private set; }

    [RelayCommand]
    [property: RunicInclude]
    private void Retry() => Retries++;

    [RelayCommand]
    private void ShowHelp() => HelpShown++;
}

public sealed partial class ImportResultWindow : RunicWindow<ImportResultViewModel>;
