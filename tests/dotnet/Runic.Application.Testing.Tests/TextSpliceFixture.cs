using System.ComponentModel;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// text-splice/1 (specs/application/state-changes.md, "Text splices"): a View with a settable text member, a read-only one
// and a member of another type whose wire value is a string.
public sealed class TextSpliceViewModel : INotifyPropertyChanged
{
    private string _body = "";
    private string? _status;
    private Guid _id;

    public string Body
    {
        get => _body;
        set { _body = value; PropertyChanged?.Invoke(this, new(nameof(Body))); }
    }

    public string? Status
    {
        get => _status;
        private set { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); }
    }

    public Guid Id
    {
        get => _id;
        set { _id = value; PropertyChanged?.Invoke(this, new(nameof(Id))); }
    }

    public void Report(string? status) => Status = status;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class TextSpliceWindow : RunicWindow<TextSpliceViewModel>;
