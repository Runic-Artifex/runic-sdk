using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Navigation;

namespace WidgetTool;

// A small status widget of a background service: its name, its process and how long it runs, and a ping. Each running
// widget is a process of its own, so two widgets on one dashboard keep their own state.
public sealed class WidgetViewModel : ReactiveObject, IDisposable
{
    private readonly ITimer _timer;
    private int _seconds;
    private int _pings;

    public WidgetViewModel(IRunicModelContext context, WidgetOptions options, TimeProvider time)
    {
        Name = options.Name;
        ProcessId = Environment.ProcessId;
        _timer = time.CreateTimer(_ => context.TryPost(() => Seconds++), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        PingCommand = ReactiveCommand.Create(() => { Pings++; });
    }

    public string Name { get; }

    public int ProcessId { get; }

    public int Seconds { get => _seconds; private set => this.RaiseAndSetIfChanged(ref _seconds, value); }

    public int Pings { get => _pings; private set => this.RaiseAndSetIfChanged(ref _pings, value); }

    public ReactiveCommand<RxVoid, RxVoid> PingCommand { get; }

    public void Dispose()
    {
        _timer.Dispose();
        PingCommand.Dispose();
    }
}

// The service the widget reports on, from its command line (Program.cs).
public sealed record WidgetOptions(string Name);
