using System.ComponentModel;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

public sealed record WindowParameterRequest(string Title);

// A Window whose ViewModel takes the parameter its open handed to its scope.
public sealed class WindowParameterViewModel(IRunicWindowParameter<WindowParameterRequest> request) : INotifyPropertyChanged
{
    public string Title { get; } = request.Value.Title;

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class WindowParameterWindow : RunicWindow<WindowParameterViewModel>;
