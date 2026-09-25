using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReactiveUI;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Runic.Application.CsWebUi.Tests")]

namespace Runic.Application.Bridge.PostMvvmFixture;

// These are private fixture bases. They deliberately do not choose a public
// Window/View authoring model.
internal abstract class ExplicitWindow<TViewModel> { }
internal abstract class ExplicitView<TViewModel> { }
internal sealed class NotesWindow : ExplicitWindow<NotesViewModel> { }
internal sealed class NotesView : ExplicitView<NotesViewModel> { }

internal sealed partial class NotesViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "Draft";

#if POST_MVVM_NATIVE_RESTART
    // The disposable browser fixture flips this compiled contract bit to force
    // dotnet watch to regenerate a different ready fingerprint.
    [ObservableProperty]
    private string _restartMarker = "replacement";
#endif

    public ReactiveCommand<string, string> RefreshCommand { get; } =
        ReactiveCommand.Create<string, string>(static value => value);

    internal int SaveCount { get; private set; }

    [RelayCommand]
    private Task SaveAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}
