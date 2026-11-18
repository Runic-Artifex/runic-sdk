using CommunityToolkit.Mvvm.ComponentModel;

namespace AotSize.Models;

// The types every fixture ViewModel shares, so that only the per-ViewModel cost of the Bridge grows with K.

// A row of the keyed collection.
public sealed record Row(int Id, string Label);

// The observed sub-object.
public sealed partial class Detail : ObservableObject
{
    [ObservableProperty] private string note = "";
}

// The input of the view request.
public sealed record FocusRow(int Id);

// The settable enum member.
public enum Priority { Low, Normal, High }

// The settable record member, whose fields the page writes one by one (field paths).
public sealed record Address(string Street, string City, int Zip);
