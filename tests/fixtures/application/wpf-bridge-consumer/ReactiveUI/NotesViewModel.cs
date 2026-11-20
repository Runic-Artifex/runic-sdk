using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;

namespace WpfReactive;

// A bridged ReactiveObject: a property raised with RaiseAndSetIfChanged and a ReactiveCommand, whose generated
// member calls the helpers of the Runic ReactiveUI adapter of the inferred flavor.
public sealed class NotesViewModel : ReactiveObject
{
    private string _title = "";

    public NotesViewModel() => SaveCommand = ReactiveCommand.Create(() => { Title = Title.Trim(); });

    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> SaveCommand { get; }
}

public sealed partial class NotesWebView : RunicView<NotesViewModel>;
