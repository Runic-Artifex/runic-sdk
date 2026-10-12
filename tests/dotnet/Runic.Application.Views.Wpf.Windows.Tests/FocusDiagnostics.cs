using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Wpf;
using Runic.Desktop;
using System.Windows;
using TUnit.Core;

// TEMPORARY (#216 evidence): repeats TabInto and samples the focused element on the script's arrival.
internal sealed class FocusDiagnostics
{
    [Test, NotInParallel("WpfSuite", Order = 13)]
    public async Task TabIntoFocusTiming()
    {
        using var child = new RunicWebView();
        var window = new Window { Content = child, Width = 600, Height = 400, Title = "Runic WPF focus timing" };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        child.Loaded += (_, _) => loaded.TrySetResult();
        window.Show();
        try
        {
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var collection = new ServiceCollection();
            collection.AddSingleton<Func<IBridgeTransport, object, IDisposable>>((_, _) => new Nothing());
            await using var services = collection.BuildServiceProvider();
            await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = child.CreateWindowHostFactory(), ConnectionTimeout = TimeSpan.FromSeconds(20) });
            await using var view = await services.CreateWpfViewAsync(host, Program.HtmlOptions(), new object());
            await view.OpenAsync();
            Emit($"[focus-diag] start windowActive={window.IsActive} keyboardFocus={System.Windows.Input.Keyboard.FocusedElement?.GetType().Name ?? "-"}");
            int resizedEarly = 0, plainEarly = 0, resizedMissing = 0, plainMissing = 0;
            List<string> lines = [];
            for (var i = 0; i < 60; i++)
            {
                var resize = i % 2 == 0;
                var reset = await view.Surface.ExecuteJavaScriptAsync("document.activeElement?.blur(); return document.activeElement?.id || '-';");
                if (resize) { child.Width = i % 4 == 0 ? 350 : 360; window.UpdateLayout(); }
                Program.Check(((System.Windows.Interop.IKeyboardInputSink)child).TabInto(
                    new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next)), "TabInto refused.");
                var focus = await view.Surface.ExecuteJavaScriptAsync(NativeChecks.FocusProbe, TimeSpan.FromSeconds(15));
                var early = !focus.Contains("initial=editor", StringComparison.Ordinal);
                var missing = !focus.StartsWith("editor ", StringComparison.Ordinal);
                if (early) { if (resize) resizedEarly++; else plainEarly++; }
                if (missing) { if (resize) resizedMissing++; else plainMissing++; }
                lines.Add($"[focus-diag] {i} resize={resize} reset={reset} {focus} windowActive={window.IsActive}");
            }
            foreach (var line in lines) Emit(line);
            Emit($"[focus-diag] summary: probe arrived before focus {resizedEarly}/30 (resize), {plainEarly}/30 (no resize); never focused {resizedMissing}/30, {plainMissing}/30");
        }
        finally { window.Close(); }
    }

    private sealed class Nothing : IDisposable { public void Dispose() { } }

    // TUnit captures Console output of passing cases; write to the process stdout so the CI log shows it.
    private static readonly System.IO.StreamWriter Raw = new(Console.OpenStandardOutput()) { AutoFlush = true };
    internal static void Emit(string line) { lock (Raw) Raw.WriteLine(line); }
}
