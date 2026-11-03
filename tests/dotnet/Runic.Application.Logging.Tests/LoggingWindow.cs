using System.ComponentModel;
using Runic.Application.Views;

namespace Runic.Application.Logging.Tests;

// The application's one Window. It guards its close, which a serve-only surface cannot honor, so opening it logs
// the Views event 1053 (WindowCloseGuardUnavailable).
public sealed partial class LoggingWindow : RunicWindow<LoggingViewModel>
{
    public LoggingWindow() => GuardClose = true;
}

// A second Window, which the application opens after the first with RUNIC_TEST_SECOND_WINDOW=1, so a serve-only
// test sees two URLs and their Window types.
public sealed partial class SecondLoggingWindow : RunicWindow<SecondLoggingViewModel>;

public sealed class LoggingViewModel : INotifyPropertyChanged
{
    public string Title { get; set; } = "Logging";

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed class SecondLoggingViewModel : INotifyPropertyChanged
{
    public string Title { get; set; } = "Second";

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}
