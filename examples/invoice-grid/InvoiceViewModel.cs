using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace InvoiceGrid;

/// <summary>The billing address. The page edits each member as its own field: <c>InvoiceFields.address.street</c>.</summary>
public sealed record InvoiceAddress(
    [property: Required, MaxLength(80)] string Street,
    [property: Required, MaxLength(10)] string PostalCode,
    [property: Required, MaxLength(60)] string City);

/// <summary>
/// One line of the invoice: an immutable row that the page edits cell by cell
/// (<c>InvoiceFields.lines.at(id).quantity</c>). A line reports its own errors, which the page shows at the cell.
/// </summary>
public sealed record InvoiceLine(int Id, string Product, int Quantity, decimal Price) : INotifyDataErrorInfo
{
    [RunicIgnore]
    public bool HasErrors => GetErrors(nameof(Quantity)).Cast<object>().Any() || GetErrors(nameof(Price)).Cast<object>().Any();

    event EventHandler<DataErrorsChangedEventArgs>? INotifyDataErrorInfo.ErrorsChanged { add { } remove { } }

    // A line never changes; a new line replaces it, and the window publishes the new line's errors.
    public IEnumerable GetErrors(string? propertyName) => propertyName switch
    {
        nameof(Quantity) when Quantity < 1 => new[] { "Order at least one." },
        nameof(Price) when Price < 0 => new[] { "The price cannot be negative." },
        nameof(Product) when string.IsNullOrWhiteSpace(Product) => new[] { "Name the product." },
        _ => Array.Empty<string>(),
    };
}

/// <summary>An invoice: an address, editable lines and their total.</summary>
public sealed partial class InvoiceViewModel : ObservableObject
{
    private int _nextId = 4;

    [ObservableProperty]
    public partial InvoiceAddress Address { get; set; } = new("Bahnhofstrasse 1", "8001", "Zürich");

    [RunicCollection(nameof(InvoiceLine.Id))]
    public ObservableCollection<InvoiceLine> Lines { get; } =
    [
        new(1, "Notebook", 3, 4.50m),
        new(2, "Fountain pen", 1, 39.00m),
        new(3, "Ink, blue", 2, 6.20m),
    ];

    /// <summary>The sum of the lines, recomputed by <see cref="UpdateLine"/>.</summary>
    public string Total => Lines.Sum(line => line.Quantity * line.Price).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// The row editor: the page changed one cell of a line, and this replaces the line. It is the one place that
    /// accepts the page's edits of rows, so it normalizes them and keeps the total in step. Throw to refuse an edit.
    /// </summary>
    [RunicRowEditor]
    internal void UpdateLine(int id, InvoiceLine line)
    {
        if (line.Quantity > 10_000) throw new ArgumentException("At most 10,000 per line.");
        var index = Lines.IndexOf(Lines.Single(existing => existing.Id == id));
        Lines[index] = line with { Product = line.Product.Trim(), Price = decimal.Round(line.Price, 2) };
        OnPropertyChanged(nameof(Total));
    }

    [RelayCommand]
    private void AddLine()
    {
        Lines.Add(new(_nextId++, "", 1, 0m));
        OnPropertyChanged(nameof(Total));
    }

    [RelayCommand]
    private void RemoveLine(int id)
    {
        if (Lines.FirstOrDefault(line => line.Id == id) is { } line) Lines.Remove(line);
        OnPropertyChanged(nameof(Total));
    }
}
