using System.ComponentModel;
using Runic.Application.Views;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests;

// B1: a ViewModel that takes its Window's presentation and records each activity change with whether it ran in a turn.
public sealed class WindowPresentationViewModel : INotifyPropertyChanged
{
    private readonly List<(RunicWindowActivity Activity, bool InTurn)> _changes = [];

    public WindowPresentationViewModel(IRunicWindowPresentation window)
    {
        Window = window;
        window.ActivityChanged += (_, args) =>
        {
            lock (_changes) _changes.Add((args.Activity, RunicModelContext.Current is not null));
        };
    }

    [RunicIgnore]
    public IRunicWindowPresentation Window { get; }

    public string Title { get; } = "presentation";

    [RunicIgnore]
    public IReadOnlyList<(RunicWindowActivity Activity, bool InTurn)> Changes
    {
        get { lock (_changes) return [.. _changes]; }
    }

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class WindowPresentationWindow : RunicWindow<WindowPresentationViewModel>;
