using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

public sealed class ValidationViewModel : ValidationNode
{
    [RunicAlias("model-profile")]
    public ValidationProfile Profile { get; } = new();

    public ObservableCollection<ValidationItem> Items { get; } = [new()];

    public Dictionary<string, ValidationItem> Lookup { get; } = new(StringComparer.Ordinal)
    {
        ["primary"] = new(),
    };

    // This object deliberately has no INotifyPropertyChanged implementation.
    // Its ErrorsChanged event must still publish the generated validation tree.
    public ValidationErrorsOnly Server { get; } = new();
}

public sealed class ValidationProfile : ValidationNode
{
    [RunicAlias("postal-code")]
    public string PostalCode { get; set; } = string.Empty;
}

public sealed class ValidationItem : ValidationNode
{
    [RunicAlias("label")]
    public string Name { get; set; } = string.Empty;
}

public sealed class ValidationErrorsOnly : INotifyDataErrorInfo
{
    private readonly Dictionary<string, List<object>> _errors = new();

    public string Detail { get; set; } = string.Empty;
    [RunicIgnore]
    public bool HasErrors => _errors.Values.Any(values => values.Count > 0);
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public IEnumerable GetErrors(string? propertyName) =>
        _errors.TryGetValue(Key(propertyName), out var values) ? values : [];

    public void SetErrors(string? propertyName, params object[] values)
    {
        _errors[Key(propertyName)] = [.. values];
        ErrorsChanged?.Invoke(this, new(propertyName));
    }

    private static string Key(string? propertyName) => propertyName ?? string.Empty;
}

public abstract class ValidationNode : INotifyPropertyChanged, INotifyDataErrorInfo
{
    private readonly Dictionary<string, List<object>> _errors = new();

    [RunicIgnore]
    public bool HasErrors => _errors.Values.Any(values => values.Count > 0);
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public IEnumerable GetErrors(string? propertyName) =>
        _errors.TryGetValue(Key(propertyName), out var values) ? values : [];

    public void SetErrors(string? propertyName, params object[] values)
    {
        _errors[Key(propertyName)] = [.. values];
        ErrorsChanged?.Invoke(this, new(propertyName));
    }

    protected void Raise(string propertyName) => PropertyChanged?.Invoke(this, new(propertyName));

    private static string Key(string? propertyName) => propertyName ?? string.Empty;
}

public sealed partial class ValidationWindow(ValidationViewModel model) : RunicWindow<ValidationViewModel>(model);
