using System.Collections;
using System.ComponentModel;
using System.Windows.Input;
using ReactiveUI.Validation.Collections;
using ReactiveUI.Validation.Extensions;
using ReactiveUI.Validation.Helpers;
using ReactiveUI.Validation.States;
using Runic.Application.Views;
using Runic.Translations;

namespace Runic.Application.Testing.Tests;

// translation-text/1 (E5): TranslationReference and RunicTextReference values, validation messages with a text
// reference, and the window's locale.
internal static class TranslatedText
{
    internal const string Catalog = "notes";
    internal const string Fingerprint = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    internal static TranslationReference Reference(string key, string fallback, int? max = null) =>
        new(Catalog, Fingerprint, key,
            max is { } value
                ? new Dictionary<string, TranslationReferenceArgument> { ["max"] = new(TextArgumentType.Int, value.ToString(System.Globalization.CultureInfo.InvariantCulture)) }
                : new Dictionary<string, TranslationReferenceArgument>(),
            fallback);

    internal static RunicTextReference Text(TranslationReference reference) =>
        RunicTextReference.Create(reference, TranslationReferenceJsonContext.Default.TranslationReference);

    internal static readonly TranslationReference TitleTooLong =
        Reference("title_too_long", "Title must be at most 8 characters.", 8);
}

public sealed record TranslatedEntry(string Name, TranslationReference Label, RunicTextReference? Note);

public sealed class TranslatedTextViewModel : INotifyPropertyChanged, INotifyDataErrorInfo
{
    private readonly IRunicWindowLocale? _locale;

    public TranslatedTextViewModel() : this(null) { }

    public TranslatedTextViewModel(IRunicWindowLocale? locale)
    {
        _locale = locale;
        // The application chooses the window's language before the window opens.
        if (locale is not null) locale.Locale = "de";
        SaveCommand = new SaveTextCommand(this);
    }

    public TranslationReference Status { get; set; } = TranslatedText.Reference("ready", "Ready.");
    public TranslationReference? Previous { get; set; }
    public RunicTextReference? Note { get; set; }
    public IReadOnlyList<TranslatedEntry> Entries { get; set; } = [];
    public string Title
    {
        get;
        set
        {
            field = value;
            PropertyChanged?.Invoke(this, new(nameof(Title)));
            ErrorsChanged?.Invoke(this, new(nameof(Title)));
        }
    } = "";

    [RunicCommandInput(typeof(TranslationReference))]
    public ICommand SaveCommand { get; }

    public void SwitchLanguage(string locale) => _locale!.Locale = locale;

    public bool HasErrors => Title.Length > 8;

    public IEnumerable GetErrors(string? propertyName) => propertyName == nameof(Title) && Title.Length > 8
        ? new object[]
        {
            new RunicValidationMessage("Title must be at most 8 characters.", "title-too-long")
            {
                Text = TranslatedText.Text(TranslatedText.TitleTooLong),
            },
            new RunicValidationResult("Title is not unique.", TranslatedText.Text(TranslatedText.Reference("title_taken", "Title is not unique."))),
        }
        : [];

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    internal void Raise(string name) => PropertyChanged?.Invoke(this, new(name));

    private sealed class SaveTextCommand(TranslatedTextViewModel owner) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            if (parameter is not TranslationReference status) throw new ArgumentException("A status is required.");
            owner.Previous = owner.Status;
            owner.Status = status;
            owner.Raise(nameof(Status));
        }
    }
}

public sealed partial class TranslatedTextWindow : RunicWindow<TranslatedTextViewModel>;

// ReactiveUI.Validation: a rule whose text carries a text reference.
public sealed class TranslatedReactiveViewModel : ReactiveValidationObject
{
    private readonly StateSignal _state = new();

    public TranslatedReactiveViewModel() => this.ValidationRule(vm => vm.Title, _state);

    public string Title
    {
        get;
        set
        {
            ReactiveUI.IReactiveObjectExtensions.RaiseAndSetIfChanged(this, ref field, value);
            _state.Value = value.Length <= 8 ? ValidationState.Valid
                : new ValidationState(false, new TranslatedValidationText(TranslatedText.TitleTooLong));
        }
    } = string.Empty;

    private sealed class StateSignal : IObservable<IValidationState>
    {
        private readonly List<IObserver<IValidationState>> _observers = [];
        private IValidationState _value = ValidationState.Valid;

        internal IValidationState Value
        {
            set
            {
                _value = value;
                foreach (var observer in _observers.ToArray()) observer.OnNext(value);
            }
        }

        public IDisposable Subscribe(IObserver<IValidationState> observer)
        {
            _observers.Add(observer);
            observer.OnNext(_value);
            return new Unsubscriber(() => _observers.Remove(observer));
        }

        private sealed class Unsubscriber(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}

public sealed partial class TranslatedReactiveWindow : RunicWindow<TranslatedReactiveViewModel>;

// What an application writes for ReactiveUI.Validation: an IValidationText that also carries the text reference.
internal sealed class TranslatedValidationText(TranslationReference reference) : IValidationText, IRunicTextReferenceSource
{
    private readonly string _message = reference.FallbackText ?? reference.Key;

    public RunicTextReference? Text { get; } = TranslatedText.Text(reference);
    public int Count => 1;
    public string this[int index] => index == 0 ? _message : throw new ArgumentOutOfRangeException(nameof(index));
    public IEnumerator<string> GetEnumerator() { yield return _message; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public string ToSingleLine(string? separator) => _message;
}

// translation-text/1: references of the catalog in packages/web/views/test/fixtures/translations, whose state the page
// test (translated-text.esm.test.ts) resolves with the real generated Translations ESM.
public sealed class TranslatedCatalogViewModel : INotifyPropertyChanged, INotifyDataErrorInfo
{
    internal const string Fingerprint = "sha256:7f0e763ef590d9331b0b2b6650705885fcb0409e0e28dd70805ca74df2c1fb93";

    private static TranslationReference Reference(string key, Dictionary<string, TranslationReferenceArgument> arguments,
        string? fallback) => new("notes", Fingerprint, key, arguments, fallback);

    private static readonly TranslationReference TitleTooLong = Reference("title_too_long",
        new() { ["max"] = new(TextArgumentType.Int, "8") }, "Title must be at most 8 characters.");

    public TranslationReference Saved { get; } = Reference("saved", new() { ["title"] = new(TextArgumentType.String, "Atlas") }, "Saved Atlas.");
    public TranslationReference Changed { get; } = Reference("changed", new() { ["packages"] = new(["firefox", "gimp"]) }, "Changed firefox, gimp");
    public TranslationReference Failed { get; } = Reference("failed", new()
    {
        ["step"] = new(Reference("steps_build", new() { ["name"] = new(TextArgumentType.String, "gimp") }, "Building gimp")),
    }, "Building gimp failed");
    // A later transport version than the page's runtime reads: the page shows its fallback text.
    public RunicTextReference Future { get; } = RunicTextReference.Parse(
        """{"version":3,"catalog":"notes","contractFingerprint":"sha256:7f0e763ef590d9331b0b2b6650705885fcb0409e0e28dd70805ca74df2c1fb93","key":"saved","arguments":{"title":{"type":"string","value":"Atlas"},"tone":{"type":"emphasis","value":"strong"}},"fallbackText":"Saved Atlas (v3)."}""");
    public string Title { get; set; } = "much too long";

    public bool HasErrors => true;
    public IEnumerable GetErrors(string? propertyName) => propertyName == nameof(Title)
        ? new[] { new RunicValidationMessage(TitleTooLong.FallbackText!, "title-too-long") { Text = TranslatedText.Text(TitleTooLong) } }
        : Array.Empty<RunicValidationMessage>();

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged { add { } remove { } }
}

public sealed partial class TranslatedCatalogWindow : RunicWindow<TranslatedCatalogViewModel>;
