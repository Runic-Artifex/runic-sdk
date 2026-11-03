using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using ReactiveUI;
using ReactiveUI.Validation.Extensions;
using ReactiveUI.Validation.Helpers;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// 0.16 C4 (field-paths/1): an invoice with an address and editable lines. Rows are immutable records; a line reports
// its own errors, so cells' errors work with any MVVM flavor.
public sealed record PathAddress([property: Required, MaxLength(40)] string Street, string City, PathGeo? Geo = null);

public sealed record PathGeo(double Lat, double Lng);

public sealed record PathLine(int Id, string Product, int Quantity, decimal Price) : INotifyDataErrorInfo
{
    public const string QuantityTooSmall = "Quantity must be at least 1.";

    [RunicIgnore]
    public bool HasErrors => Quantity < 1;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged { add { } remove { } }

    public IEnumerable GetErrors(string? propertyName) =>
        Quantity < 1 && propertyName is nameof(Quantity) ? new[] { QuantityTooSmall } : Array.Empty<string>();
}

// The CommunityToolkit flavor: an ObservableObject whose row editor recomputes the total.
public sealed partial class PathInvoiceViewModel : ObservableObject
{
    public const string NegativePrice = "The price must not be negative.";

    [ObservableProperty]
    public partial PathAddress Address { get; set; } = new("", "Bern");

    [RunicCollection(nameof(PathLine.Id))]
    public ObservableCollection<PathLine> Lines { get; } = [new(1, "Pens", 2, 1.50m), new(2, "Ink", 1, 4.00m)];

    [ObservableProperty]
    public partial decimal Total { get; set; } = 7.00m;

    // Updates for the rows reach the page by Lines' replace and the total by its notification.
    [RunicRowEditor]
    internal void UpdateLine(int id, PathLine line)
    {
        if (line.Price < 0) throw new ArgumentException(NegativePrice);
        var index = Lines.IndexOf(Lines.Single(existing => existing.Id == id));
        Lines[index] = line with { Product = line.Product.Trim() };
        Total = Lines.Sum(item => item.Quantity * item.Price);
    }
}

public sealed partial class PathInvoiceWindow : RunicWindow<PathInvoiceViewModel>;

// The ReactiveUI flavor: a mutable address with its own ReactiveUI.Validation rule, which a path write rebuilds.
public sealed class ReactivePathAddress : ReactiveValidationObject
{
    public const string StreetRequired = "Street is required.";

    public ReactivePathAddress() => this.ValidationRule(address => address.Street, street => !string.IsNullOrWhiteSpace(street), StreetRequired);

    public string Street { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string City { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "";
}

public sealed class ReactivePathInvoiceViewModel : ReactiveValidationObject
{
    public ReactivePathAddress Address { get; set => this.RaiseAndSetIfChanged(ref field, value); } = new();

    [RunicCollection(nameof(PathLine.Id))]
    public ObservableCollection<PathLine> Lines { get; } = [new(1, "Pens", 2, 1.50m)];

    [RunicRowEditor(nameof(Lines))]
    public void ReplaceLine(int id, PathLine line) => Lines[Lines.IndexOf(Lines.Single(existing => existing.Id == id))] = line;
}

public sealed partial class ReactivePathInvoiceWindow : RunicWindow<ReactivePathInvoiceViewModel>;
