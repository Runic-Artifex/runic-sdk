using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// Values the page only sends (#312): nullable members and parameters with a
// default are optional in TypeScript, and .NET fills them in when they are missing.
public enum OptionalScope { All, Home, System }

public sealed record OptionalRequest(string Kind, string? Label = null, int Retention = 3, bool? Edits = null,
    JsonElement Extra = default, OptionalScope Scope = OptionalScope.Home, double Ratio = 0.5, string Note = "none");

public sealed class OptionalSettings
{
    public string Name { get; init; } = "settings";
    public int? Limit { get; init; } = 7;
    public string? Note { get; set; }
}

// Written to the page only, so it needs no constructor for its members (#312).
public sealed class OptionalSummary(int runs)
{
    public int Runs { get; } = runs;
    public string Text => $"{Runs} runs";
}

// A JsonElement member without a value is absent from the page's object (#312).
public sealed record OptionalVersion(string Name, JsonElement Previous);

public sealed partial class OptionalMemberViewModel : ObservableObject
{
    [RunicIgnore] public OptionalRequest? LastRequest { get; private set; }
    [RunicIgnore] public OptionalSettings? LastSettings { get; private set; }

    [ObservableProperty]
    public partial OptionalSummary Summary { get; private set; } = new(0);

    public IReadOnlyList<OptionalVersion> Versions { get; } =
    [
        new("new", default),
        new("removed", JsonDocument.Parse("null").RootElement.Clone()),
        new("updated", JsonDocument.Parse("\"1.0\"").RootElement.Clone()),
    ];

    [RelayCommand]
    private void Submit(OptionalRequest request)
    {
        LastRequest = request;
        Summary = new(Summary.Runs + 1);
    }

    [RelayCommand]
    private void Configure(OptionalSettings settings) => LastSettings = settings;
}

public sealed partial class OptionalMemberWindow : RunicWindow<OptionalMemberViewModel>;
