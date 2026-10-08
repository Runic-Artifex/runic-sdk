using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HybridNotes;
using HybridNotes.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Runic.Navigation;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exit = 0;
        app.DispatcherUnhandledException += (_, e) => { Console.Error.WriteLine(e.Exception); exit = 1; e.Handled = true; app.Shutdown(); };
        app.Startup += async (_, _) =>
        {
            try
            {
                await RunAsync().WaitAsync(TimeSpan.FromSeconds(60));
                Console.WriteLine("PASS WPF shell + embedded note editor: swap, web save, reload, unload");
            }
            catch (Exception error) { Console.Error.WriteLine(error); exit = 1; }
            finally { app.Shutdown(); }
        };
        app.Run();
        return exit;
    }

    private static async Task RunAsync()
    {
        await using var services = App.AddServices(new ServiceCollection()).BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var regions = scope.ServiceProvider.GetRequiredService<AppRegions>();
        var context = scope.ServiceProvider.GetRequiredService<IRunicModelContext>();
        var store = scope.ServiceProvider.GetRequiredService<INoteStore>();
        var window = new MainWindow(regions) { DataContext = regions, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        window.Show();
        try
        {
            await regions.Main.ResetAsync<NotesListViewModel>();
            await regions.Main.PushAsync<EditorViewModel, int>(1);
            var model = (EditorViewModel)regions.Main.Current!;
            var entry = regions.Main.CurrentEntry;
            await UntilAsync(() => Descendants<EditorView>(window).Any());
            var page = Descendants<EditorView>(window).Single();
            await page.PresentationChange;
            // Actual native TextBox binding, then the actual presentation toggle button.
            var native = Descendants<NativeEditorView>(page).Single();
            Descendants<TextBox>(native).First().Text = "Native draft";
            Check(model.Title == "Native draft" && model.IsDirty, "Native edit did not reach the existing model.");
            ((Button)page.FindName("Switch")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.PresentationChange;
            var first = page.WebBinding ?? throw new InvalidOperationException("Embedded editor did not open.");
            Check(ReferenceEquals(first.ViewModel, model) && ReferenceEquals(regions.Main.CurrentEntry, entry), "Presentation switch replaced model or entry.");
            await UntilAsync(async () => await first.Surface.ExecuteJavaScriptAsync("return document.querySelector('#title').value;") == "Native draft");

            await first.Surface.RunJavaScriptAsync("""
                const title = document.querySelector('#title');
                title.value = 'Saved from web'; title.dispatchEvent(new Event('input'));
                const body = document.querySelector('#body');
                body.value = 'One shared draft'; body.dispatchEvent(new Event('input'));
                document.querySelector('#save').click();
                """);
            await UntilAsync(() => store.Get(1).Title == "Saved from web" && !model.IsDirty);
            Check(model.Body == "One shared draft", "Web edits did not reach the native model.");

            ((Button)page.FindName("Switch")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.PresentationChange;
            Check(page.WebBinding is null && Descendants<NativeEditorView>(page).Single().DataContext == model, "Switch to native retained the web session or replaced the model.");
            await (await first.CloseAsync(TimeSpan.Zero)).Completion;
            ((Button)page.FindName("Switch")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.PresentationChange;
            var second = page.WebBinding ?? throw new InvalidOperationException("Second embedded session did not open.");
            Check(!ReferenceEquals(first, second), "Switch reused a closed web session.");
            ((Button)page.FindName("Reload")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.PresentationChange;
            var reloaded = page.WebBinding ?? throw new InvalidOperationException("Reload did not open.");
            Check(!ReferenceEquals(second, reloaded) && ReferenceEquals(reloaded.ViewModel, model), "Reload did not preserve the same model in a fresh session.");
            await UntilAsync(async () => await reloaded.Surface.ExecuteJavaScriptAsync("return document.querySelector('#body').value;") == "One shared draft");
            Check(ReferenceEquals(regions.Main.CurrentEntry, entry), "Reload changed navigation entry.");

            await regions.Main.BackAsync();
            await UntilAsync(() => !page.IsLoaded);
            await page.PresentationChange;
            Check(page.WebBinding is null, "Unload left a web binding attached.");
            await (await reloaded.CloseAsync(TimeSpan.Zero)).Completion;
            await context.InvokeAsync(() => Check(regions.Main.Current is NotesListViewModel, "Presentation unload disposed the borrowed context or navigator."));
        }
        finally { window.Close(); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
        {
            var child = VisualTreeHelper.GetChild(node, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static Task UntilAsync(Func<bool> ready) => UntilAsync(() => Task.FromResult(ready()));
    private static async Task UntilAsync(Func<Task<bool>> ready)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await ready())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Hybrid editor did not reach the expected state.");
            await Task.Delay(20);
        }
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
