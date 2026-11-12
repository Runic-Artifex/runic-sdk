using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// #863: union cases with list members. The generated encoders, test double
// included, must compile under strict TypeScript: a list, dictionary, optional
// list or nested union inside a case is encoded from a typed case value.
public sealed record UnionListPage(string Id, IReadOnlyList<int> Marks);

[RunicUnion(typeof(UnionListNote), typeof(UnionListLink))]
public abstract record UnionListTarget;
[RunicUnionCase("note")] public sealed record UnionListNote(IReadOnlyList<string> Lines) : UnionListTarget;
[RunicUnionCase("link")] public sealed record UnionListLink(string Href) : UnionListTarget;

[RunicUnion(typeof(UnionListRows), typeof(UnionListRail), typeof(UnionListEmpty))]
public abstract record UnionListArea(string Id, IReadOnlyList<string> Pages);
[RunicUnionCase("rows")]
public sealed record UnionListRows(string Id, IReadOnlyList<string> Pages, IReadOnlyList<UnionListPage> Entries,
    IReadOnlyList<UnionListTarget> Targets) : UnionListArea(Id, Pages);
[RunicUnionCase("rail")]
public sealed record UnionListRail(string Id, IReadOnlyList<string> Pages, IReadOnlyList<string> Phases,
    IReadOnlyDictionary<string, IReadOnlyList<UnionListPage>> Groups, UnionListTarget Target,
    IReadOnlyList<string>? Hints = null) : UnionListArea(Id, Pages);
[RunicUnionCase("empty")]
public sealed record UnionListEmpty(string Id, IReadOnlyList<string> Pages) : UnionListArea(Id, Pages);

// Areas reach the page as state and come back as a command argument, so both
// the decoder and the encoder (the client's and the test double's) are emitted.
public sealed partial class UnionListViewModel : ObservableObject
{
    [ObservableProperty]
    public partial IReadOnlyList<UnionListArea> Areas { get; set; } =
    [
        new UnionListRows("rows", ["a"], [new("p", [1, 2])], [new UnionListNote(["x"]), new UnionListLink("/y")]),
        new UnionListRail("rail", ["b"], ["one"], new Dictionary<string, IReadOnlyList<UnionListPage>> { ["g"] = [new("q", [3])] },
            new UnionListNote([]), ["hint"]),
        new UnionListEmpty("empty", []),
    ];

    [RelayCommand]
    private void Replace(IReadOnlyList<UnionListArea> areas) => Areas = areas;
}

public sealed partial class UnionListWindow : RunicWindow<UnionListViewModel>;
