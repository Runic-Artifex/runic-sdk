using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// RunicValidationOptions.TextFor (S5b): a ViewModel that validates its DataAnnotations with Validator, as a
// hand-written INotifyDataErrorInfo does, and reports ValidationResults for some members and strings for others.
public sealed class ValidationTextViewModel : INotifyPropertyChanged, INotifyDataErrorInfo
{
    private readonly Dictionary<string, List<ValidationResult>> _errors = new(StringComparer.Ordinal);

    [Required, Display(Name = "Heading")]
    public string Title { get; set; } = "";

    [MaxLength(8)]
    public string Tag { get; set; } = "";

    [Range(1, 10)]
    public int Count { get; set; } = 1;

    // A message the ViewModel writes itself matches no attribute.
    [Required]
    public string Note { get; set; } = "";

    public ValidationTextDetails Details { get; } = new();

    public void Validate()
    {
        _errors.Clear();
        foreach (var (name, value) in new (string, object?)[] { (nameof(Title), Title), (nameof(Tag), Tag), (nameof(Count), Count) })
        {
            var results = new List<ValidationResult>();
            if (!Validator.TryValidateProperty(value, new ValidationContext(this) { MemberName = name }, results))
                _errors[name] = results;
        }
        if (Note.Length == 0) _errors[nameof(Note)] = [new("Please write a note.", [nameof(Note)])];
        Details.Validate();
        foreach (var name in new[] { nameof(Title), nameof(Tag), nameof(Count), nameof(Note) })
        {
            ErrorsChanged?.Invoke(this, new(name));
            PropertyChanged?.Invoke(this, new(name));
        }
    }

    public bool HasErrors => _errors.Count > 0;

    // Tag's errors are strings, the others ValidationResults.
    public IEnumerable GetErrors(string? propertyName) =>
        propertyName is null ? _errors.Values.SelectMany(results => results).ToArray()
        : !_errors.TryGetValue(propertyName, out var results) ? Array.Empty<object>()
        : propertyName == nameof(Tag) ? results.Select(result => (object)result.ErrorMessage!).ToArray()
        : results.ToArray();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
}

// A DTO member with its own validation: its attributes are found through the generated DTO metadata.
public sealed class ValidationTextDetails : INotifyPropertyChanged, INotifyDataErrorInfo
{
    private List<ValidationResult> _errors = [];

    [StringLength(4, MinimumLength = 2), Display(Name = "Postcode")]
    public string Code { get; set; } = "";

    internal void Validate()
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateProperty(Code, new ValidationContext(this) { MemberName = nameof(Code) }, results);
        _errors = results;
        ErrorsChanged?.Invoke(this, new(nameof(Code)));
    }

    public bool HasErrors => _errors.Count > 0;
    public IEnumerable GetErrors(string? propertyName) => propertyName is null or nameof(Code) ? _errors : Array.Empty<object>();

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
}

public sealed partial class ValidationTextWindow : RunicWindow<ValidationTextViewModel>;
