using System.ComponentModel;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// The ViewModel of the former protocol-3 text delta fixtures: a live log and a title.
public sealed class TextDeltaViewModel : INotifyPropertyChanged
{
    private string? _log = "";
    private string _title = "log";

    /// <summary>The live log.</summary>
    [RunicAppendOnlyText]
    public string? Log => _log;

    public string Title
    {
        get => _title;
        set { _title = value; PropertyChanged?.Invoke(this, new(nameof(Title))); }
    }

    public void SetLog(string? value)
    {
        _log = value;
        PropertyChanged?.Invoke(this, new(nameof(Log)));
    }

    public void Append(string text) => SetLog(_log + text);

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class TextDeltaWindow : RunicWindow<TextDeltaViewModel>;

// A ViewModel with validation publishes its text in full states only.
public sealed class ValidatedTextViewModel : INotifyPropertyChanged, INotifyDataErrorInfo
{
    private string _log = "";

    [RunicAppendOnlyText]
    public string Log => _log;

    public void Append(string text)
    {
        _log += text;
        PropertyChanged?.Invoke(this, new(nameof(Log)));
    }

    public bool HasErrors => false;
    public System.Collections.IEnumerable GetErrors(string? propertyName) => Array.Empty<string>();
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged { add { } remove { } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class ValidatedTextWindow : RunicWindow<ValidatedTextViewModel>;
