using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// RunicValidationOptions.TextFor (S5b): a ViewModel that validates its DataAnnotations with Validator, as a
// hand-written INotifyDataErrorInfo does, and reports ValidationResults for some members and strings for others.
public sealed class ValidationTextViewModel : ValidationTextBase, INotifyPropertyChanged, INotifyDataErrorInfo
{
    private static readonly string[] Members =
        [nameof(Title), nameof(Tag), nameof(Count), nameof(Ratio), nameof(Price), nameof(Password), nameof(Confirm), nameof(Handle), nameof(Alias)];

    private readonly Dictionary<string, List<ValidationResult>> _errors = new(StringComparer.Ordinal);

    [Required, Display(Name = "Heading")]
    public string Title { get; set; } = "";

    [MaxLength(8)]
    public string Tag { get; set; } = "";

    [Range(1, 10)]
    public int Count { get; set; } = 1;

    // Limits whose message depends on the culture: "0.5" in en-US, "0,5" in de-DE.
    [Range(0.5, 9.5)]
    public double Ratio { get; set; } = 1;

    // String limits, which RangeAttribute parses on its first use, in the current culture unless
    // ParseLimitsInInvariantCulture is set.
    [Range(typeof(decimal), "0.5", "9.5")]
    public decimal Price { get; set; } = 1;

    [Display(Name = "Passwort")]
    public string Password { get; set; } = "";

    // [Compare] names the other property's [Display(Name)] only once it has evaluated a value.
    [Compare(nameof(Password))]
    public string Confirm { get; set; } = "";

    // Two attributes with the same message.
    [MinLength(3, ErrorMessage = "Invalid"), MaxLength(10, ErrorMessage = "Invalid")]
    public string Handle { get; set; } = "abc";

    // Hides the base's int Alias: typeof(ValidationTextViewModel).GetProperty("Alias") is ambiguous.
    [MaxLength(4)]
    public new string Alias { get; set; } = "";

    // A message the ViewModel writes itself while the member's attributes pass matches no attribute.
    [Required]
    public string Note { get; set; } = "note";

    // An error the ViewModel adds to Confirm's, so its errors and failing attributes do not line up.
    public bool ExtraConfirmError { get; set; }

    public ValidationTextDetails Details { get; } = new();

    public ValidationTextShape? Shape { get; internal set; }

    public void Validate()
    {
        _errors.Clear();
        foreach (var name in Members)
            if (ValidationTextValidator.Validate(this, name) is { Count: > 0 } results) _errors[name] = results;
        if (ExtraConfirmError) (_errors.TryGetValue(nameof(Confirm), out var confirm) ? confirm : _errors[nameof(Confirm)] = [])
            .Add(new("Choose another password.", [nameof(Confirm)]));
        if (Note.Length < 3) _errors[nameof(Note)] = [new("Please write a longer note.", [nameof(Note)])];
        Details.Validate();
        (Shape as ValidationTextCircle)?.Validate();
        foreach (var name in Members.Append(nameof(Note)))
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

public class ValidationTextBase
{
    public int Alias { get; set; }
}

// The fixture's validator: Validator.TryValidateValue over fresh attribute instances, so that each test's culture
// formats the messages, with [Range] limits parsed in the invariant culture.
internal static class ValidationTextValidator
{
    internal static List<ValidationResult> Validate(object owner, string name)
    {
        var property = owner.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        var attributes = property.GetCustomAttributes<ValidationAttribute>(true).ToArray();
        foreach (var attribute in attributes)
            if (attribute is RangeAttribute range) range.ParseLimitsInInvariantCulture = true;
        var results = new List<ValidationResult>();
        var context = new ValidationContext(owner, ValidationTextDisplayName(property), null, null) { MemberName = name };
        Validator.TryValidateValue(property.GetValue(owner), context, results, attributes);
        return results;
    }

    private static string ValidationTextDisplayName(PropertyInfo property) =>
        property.GetCustomAttribute<DisplayAttribute>()?.GetName() ?? property.Name;
}

// A DTO member with its own validation: its attributes are found through the generated DTO metadata.
public sealed class ValidationTextDetails : INotifyPropertyChanged, INotifyDataErrorInfo
{
    private List<ValidationResult> _errors = [];

    [StringLength(4, MinimumLength = 2), Display(Name = "Postcode")]
    public string Code { get; set; } = "";

    internal void Validate()
    {
        _errors = ValidationTextValidator.Validate(this, nameof(Code));
        ErrorsChanged?.Invoke(this, new(nameof(Code)));
    }

    public bool HasErrors => _errors.Count > 0;
    public IEnumerable GetErrors(string? propertyName) => propertyName is null or nameof(Code) ? _errors : Array.Empty<object>();

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
}

// A union case's member with its own validation: the generated union metadata names it too.
[RunicUnion(typeof(ValidationTextCircle), typeof(ValidationTextSquare))]
public abstract class ValidationTextShape;

[RunicUnionCase("circle")]
public sealed class ValidationTextCircle : ValidationTextShape, INotifyPropertyChanged, INotifyDataErrorInfo
{
    private List<ValidationResult> _errors = [];

    [Range(1, 100)]
    public int Radius { get; set; } = 1;

    internal void Validate()
    {
        _errors = ValidationTextValidator.Validate(this, nameof(Radius));
        ErrorsChanged?.Invoke(this, new(nameof(Radius)));
    }

    public bool HasErrors => _errors.Count > 0;
    public IEnumerable GetErrors(string? propertyName) => propertyName is null or nameof(Radius) ? _errors : Array.Empty<object>();

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
}

[RunicUnionCase("square")]
public sealed class ValidationTextSquare : ValidationTextShape
{
    public int Side { get; set; } = 1;
}

// A validation attribute that throws outside the fixture's own validator, as one that needs a service would.
[AttributeUsage(AttributeTargets.Property)]
public sealed class ValidationTextThrowingAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) =>
        throw new InvalidOperationException("This attribute cannot be evaluated here.");
}

// Members for the hand-written metadata of the failure tests.
public sealed class ValidationTextFailures : INotifyDataErrorInfo
{
    [MaxLength(2), ValidationTextThrowing]
    public string Flaky { get; set; } = "long";

    [Required]
    public string Name { get; set; } = "";

    [Required]
    public string Other { get; set; } = "";

    public bool HasErrors => true;

    public IEnumerable GetErrors(string? propertyName) => propertyName switch
    {
        nameof(Flaky) => new[] { new ValidationResult("The field Flaky must be a string or array type with a maximum length of '2'.", [nameof(Flaky)]) },
        nameof(Name) => new[] { new ValidationResult("The Name field is required.", [nameof(Name)]) },
        nameof(Other) => new[] { new ValidationResult("The Other field is required.", [nameof(Other)]) },
        _ => Array.Empty<object>(),
    };

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged { add { } remove { } }
}

public sealed partial class ValidationTextWindow : RunicWindow<ValidationTextViewModel>;
