using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// 0.16 forms (C8, C7): an enum's value list, DataAnnotations on field descriptors and Int64 collection keys.
public enum FormsPriority { Low, Normal, High, Urgent = High }

public sealed record FormsRow(long Id, string Label);

public sealed class FormsViewModel : INotifyPropertyChanged
{
    public FormsViewModel() => Rows = new(Items);

    [Required, StringLength(40, MinimumLength = 2)]
    public string Name { get; set; } = "";
    [MaxLength(8)]
    public string Tag { get; set; } = "";
    [Range(1, 10)]
    public int Quantity { get; set; } = 1;
    [Range(typeof(decimal), "0.01", "999.99", MinimumIsExclusive = true)]
    public decimal Price { get; set; } = 1m;
    [RegularExpression("^[A-Z]{3}$")]
    public string Code { get; set; } = "ABC";
    // Inline options have no JavaScript equivalent in a pattern attribute.
    [RegularExpression("(?i)abc")]
    public string Loose { get; set; } = "abc";
    // The v flag of a browser's pattern rejects a lone - in a class, and \d is Unicode in .NET but ASCII in JavaScript.
    [RegularExpression("^[a-z-]+$")]
    public string Slug { get; set; } = "a";
    [RegularExpression(@"^\d{5}$")]
    public string Zip { get; set; } = "12345";
    [RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$")]
    public string Kebab { get; set; } = "a-b";
    [Range(typeof(DateTime), "2020-01-01", "2030-12-31 12:30")]
    public DateTime When { get; set; } = new(2026, 1, 1);
    [Range(typeof(decimal), "0.10", "1e3")]
    public decimal Rate { get; set; } = 1m;
    public FormsPriority Priority { get; set; }

    [RunicIgnore]
    public ObservableCollection<FormsRow> Items { get; } = [];
    [RunicCollection(nameof(FormsRow.Id))]
    public ReadOnlyObservableCollection<FormsRow> Rows { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Raise(string name) => PropertyChanged?.Invoke(this, new(name));
}

public sealed partial class FormsWindow : RunicWindow<FormsViewModel>;

// C3: a ViewModel whose asynchronous checks report validation in progress, on itself and on a nested DTO.
public sealed class PendingAddress : IRunicPendingValidation
{
    public string Postcode { get; set; } = "";
    [RunicIgnore]
    public RunicPendingValidation Pending { get; } = new();
    IReadOnlyCollection<string> IRunicPendingValidation.PendingMemberPaths => Pending.PendingMemberPaths;
    event EventHandler? IRunicPendingValidation.PendingChanged { add => Pending.PendingChanged += value; remove => Pending.PendingChanged -= value; }
}

public sealed class PendingFormViewModel : INotifyPropertyChanged, IRunicPendingValidation
{
    public string UserName { get; set; } = "";
    public PendingAddress Address { get; } = new();
    [RunicIgnore]
    public RunicPendingValidation Pending { get; } = new();
    IReadOnlyCollection<string> IRunicPendingValidation.PendingMemberPaths => Pending.PendingMemberPaths;
    event EventHandler? IRunicPendingValidation.PendingChanged { add => Pending.PendingChanged += value; remove => Pending.PendingChanged -= value; }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Raise(string name) => PropertyChanged?.Invoke(this, new(name));
}

public sealed partial class PendingFormWindow : RunicWindow<PendingFormViewModel>;
