using System.ComponentModel;
using Runic.Application.Views;

namespace Runic.Application.Logging.Tests;

// The application's one Window. It guards its close, which a serve-only surface cannot honor, so opening it logs
// the Views event 1053 (WindowCloseGuardUnavailable).
public sealed partial class LoggingWindow : RunicWindow<LoggingViewModel>
{
    public LoggingWindow() => GuardClose = true;
}

public sealed class LoggingViewModel : INotifyPropertyChanged
{
    public string Title { get; set; } = "Logging";

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}
