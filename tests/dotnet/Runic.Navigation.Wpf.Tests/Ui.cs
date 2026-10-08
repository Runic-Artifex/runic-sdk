using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace Runic.Navigation.Wpf.Tests;

// The pump helpers: a DispatcherFrame that ends when the condition holds, with a DispatcherTimer timeout.
internal static partial class Ui
{
    public static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

    // Every window that loaded, in order, to prove a window never opened.
    public static List<Window> LoadedWindows { get; } = [];

    public static void Initialize() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, e) =>
        {
            if (ReferenceEquals(sender, e.OriginalSource)) LoadedWindows.Add((Window)sender);
        }));

    public static void PumpUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        if (condition()) return;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow + (timeout ?? WaitLimit);
        var timedOut = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) =>
        {
            if (condition()) frame.Continue = false;
            else if (DateTime.UtcNow > deadline)
            {
                timedOut = true;
                frame.Continue = false;
            }
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (timedOut) throw new TimeoutException($"Timed out waiting for {what}.");
        if (!condition()) throw new InvalidOperationException($"The dispatcher stopped before {what}.");
    }

    public static void Pump(Task task, string what = "a task")
    {
        PumpUntil(() => task.IsCompleted, what);
        task.GetAwaiter().GetResult();
    }

    public static T Pump<T>(Task<T> task, string what = "a task")
    {
        PumpUntil(() => task.IsCompleted, what);
        return task.GetAwaiter().GetResult();
    }

    public static T Pump<T>(ValueTask<T> task, string what = "a task") => Pump(task.AsTask(), what);

    // Runs everything queued above ApplicationIdle, twice, so posted follow-ups run too.
    public static void Drain()
    {
        for (var round = 0; round < 2; round++)
        {
            var done = false;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => done = true);
            PumpUntil(() => done, "an idle dispatcher");
        }
    }

    public static Window ShowWindow(object? content = null, Action<Window>? configure = null)
    {
        var window = new Window
        {
            Left = -20000,
            Top = -20000,
            Width = 320,
            Height = 240,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false,
            ShowInTaskbar = false,
            Title = "Runic.Navigation.Wpf.Tests",
        };
        configure?.Invoke(window);
        window.Content = content;
        window.Show();
        Drain();
        return window;
    }

    public static void CloseStrayWindows(string test)
    {
        var stray = Application.Current?.Windows.Cast<Window>().ToList() ?? [];
        if (stray.Count == 0) return;
        Console.WriteLine($"  ({test} left {stray.Count} window(s) open; closing them)");
        foreach (var window in stray) window.Close();
        Drain();
    }

    public static bool IsOpen(Window window) => PresentationSource.FromVisual(window) is not null;

    public static nint Handle(Window window) => new WindowInteropHelper(window).Handle;

    public static bool IsEnabled(Window window) => IsWindowEnabled(Handle(window));

    public static DataTemplate TextBoxTemplate(Type type)
    {
        var template = new DataTemplate(type) { VisualTree = new FrameworkElementFactory(typeof(TextBox)) };
        template.Seal();
        return template;
    }

    public static void AddTemplates(FrameworkElement scope, params Type[] types)
    {
        foreach (var type in types) scope.Resources.Add(new DataTemplateKey(type), TextBoxTemplate(type));
    }

    // The root of the view the host presents.
    public static FrameworkElement? ViewOf(ContentControl host)
    {
        host.UpdateLayout();
        if (host.Content is not ContentPresenter presenter) return null;
        presenter.ApplyTemplate();
        presenter.UpdateLayout();
        if (presenter.Content is FrameworkElement view) return view;
        return VisualTreeHelper.GetChildrenCount(presenter) > 0 ? VisualTreeHelper.GetChild(presenter, 0) as FrameworkElement : null;
    }

    public static void PressEscape(Window window)
    {
        var source = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("The window isn't shown.");
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        });
    }

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T) { return true; }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowEnabled(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnableWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);
}

internal sealed class LogCapture : ILoggerFactory
{
    private readonly ConcurrentQueue<(string Category, LogLevel Level, int Id, Exception? Error)> _entries = new();

    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public ILogger<T> CreateLogger<T>() => new Logger<T>(this);

    public void Dispose()
    {
    }

    public int Count(int id) => _entries.Count(entry => entry.Id == id);

    public IEnumerable<(string Category, LogLevel Level, int Id, Exception? Error)> All(int id) => _entries.Where(entry => entry.Id == id);

    private sealed class Logger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner._entries.Enqueue((category, logLevel, eventId.Id, exception));
    }
}
