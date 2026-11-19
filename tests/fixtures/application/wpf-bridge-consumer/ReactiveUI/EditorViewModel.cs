using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;

namespace WpfReactive;

public sealed class EditorViewModel : ReactiveObject
{
    private string _title = "";

    public EditorViewModel() => SaveCommand = ReactiveCommand.Create(() => { Saved = Title; });

    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    [RunicIgnore]
    public string? Saved { get; private set; }

    // Generated against the adapter of the inferred flavor (ReactiveCommandExecution).
    public ReactiveCommand<RxVoid, RxVoid> SaveCommand { get; }
}

public sealed partial class EditorWebView : RunicView<EditorViewModel>;
