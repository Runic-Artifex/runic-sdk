using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Runic.Application.Views;

namespace Runic.Application.Views.Translations.Tests;

// A form whose built-in DataAnnotations attributes report through INotifyDataErrorInfo, as a hand-written ViewModel or
// CommunityToolkit's ObservableValidator does. The window shows their messages with the runic-views text.
public sealed class SignUpViewModel : INotifyPropertyChanged, INotifyDataErrorInfo
{
    private static readonly string[] Members = [nameof(Name), nameof(Nickname), nameof(Age), nameof(Email)];
    private readonly Dictionary<string, List<ValidationResult>> _errors = new(StringComparer.Ordinal);

    [Required, Display(Name = "Name")]
    public string Name { get; set; } = "";

    [MaxLength(8)]
    public string Nickname { get; set; } = "";

    [Range(18, 120)]
    public int Age { get; set; } = 18;

    // A message of the application's own stays.
    [EmailAddress(ErrorMessage = "Use your work address.")]
    public string Email { get; set; } = "";

    public void Validate()
    {
        _errors.Clear();
        foreach (var name in Members)
        {
            var results = new List<ValidationResult>();
            var value = GetType().GetProperty(name)!.GetValue(this);
            if (!Validator.TryValidateProperty(value, new ValidationContext(this) { MemberName = name }, results)) _errors[name] = results;
        }
        foreach (var name in Members)
        {
            ErrorsChanged?.Invoke(this, new(name));
            PropertyChanged?.Invoke(this, new(name));
        }
    }

    public bool HasErrors => _errors.Count > 0;

    public IEnumerable GetErrors(string? propertyName) =>
        propertyName is not null && _errors.TryGetValue(propertyName, out var errors) ? errors : [];

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
}

public sealed partial class SignUpWindow : RunicWindow<SignUpViewModel>;
