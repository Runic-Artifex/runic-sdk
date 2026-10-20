using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HybridNotes;
using HybridNotes.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views.Wpf;
using Runic.Navigation;
using Runic.Navigation.Wpf;

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
                await WithinAsync(RunAsync(), TimeSpan.FromSeconds(120), "the whole hybrid smoke");
                Console.WriteLine("PASS WPF shell + embedded note editor: swap, web save, reload, unload, guarded close");
            }
            catch (Exception error) { Console.Error.WriteLine(error); exit = 1; }
            finally { app.Shutdown(); }
        };
        app.Run();
        return exit;
    }

    private static async Task RunAsync()
    {
        Console.WriteLine("Hybrid smoke: create WPF shell");
        await using var services = App.AddServices(new ServiceCollection()).BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var regions = scope.ServiceProvider.GetRequiredService<AppRegions>();
        var context = scope.ServiceProvider.GetRequiredService<IRunicModelContext>();
        var store = scope.ServiceProvider.GetRequiredService<INoteStore>();
        var window = new MainWindow { DataContext = regions, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        window.Show();
        try
        {
            await regions.Main.ResetAsync<NotesListViewModel>();
            await regions.Main.PushAsync<EditorViewModel, int>(1);
            var model = (EditorViewModel)regions.Main.Current!;
            var entry = regions.Main.CurrentEntry;
            await UntilAsync("the native editor view to load", () => Descendants<NativeEditorView>(window).Any());
            var page = Descendants<EditorView>(window).Single();
            Console.WriteLine("Hybrid smoke: native editor loaded");
            // Actual native TextBox binding, then the actual presentation toggle button.
            var native = Descendants<NativeEditorView>(page).Single();
            Descendants<TextBox>(native).First().Text = "Native draft";
            Check(model.Title == "Native draft" && model.IsDirty, "Native edit did not reach the existing model.");
            Click(page, "Switch");
            var firstHost = await OpenedAsync(page, "first web mount");
            Console.WriteLine("Hybrid smoke: first embedded session opened");
            var first = firstHost.Surface!;
            Check(ReferenceEquals(firstHost.ViewModel, model) && ReferenceEquals(regions.Main.CurrentEntry, entry), "Presentation switch replaced model or entry.");
            // The session connects before the View mounts its inputs, so the poll waits for them too.
            await UntilAsync("the web title input to show the native draft", async () => await first.ExecuteJavaScriptAsync("return document.querySelector('#title')?.value ?? '';") == "Native draft");

            await first.RunJavaScriptAsync("""
                const title = document.querySelector('#title');
                title.value = 'Saved from web'; title.dispatchEvent(new Event('input'));
                const body = document.querySelector('#body');
                body.value = 'One shared draft'; body.dispatchEvent(new Event('input'));
                document.querySelector('#save').click();
                """);
            await UntilAsync("the web save to reach the store and clear IsDirty", () => store.Get(1).Title == "Saved from web" && !model.IsDirty);
            Console.WriteLine("Hybrid smoke: web edits saved in shared store");
            Check(model.Body == "One shared draft", "Web edits did not reach the native model.");

            Click(page, "Switch");
            await ReleasedAsync(firstHost, "switch to native");
            Console.WriteLine("Hybrid smoke: switched to native and released first session");
            Check(page.WebHost is null && Descendants<NativeEditorView>(page).Single().DataContext == model, "Switch to native retained the web host or replaced the model.");
            Click(page, "Switch");
            var secondHost = await OpenedAsync(page, "second web mount");
            Console.WriteLine("Hybrid smoke: second embedded session opened");
            var second = secondHost.Surface!;
            Check(!ReferenceEquals(first, second), "Switch reused a closed web session.");
            Click(page, "Reload");
            await WithinAsync(secondHost.WaitForPresentationAsync(), TimeSpan.FromSeconds(30), "the reloaded web session's presentation");
            Check(secondHost.State == RunicViewHostState.Open, $"Reload did not reopen: {secondHost.Error}");
            Console.WriteLine("Hybrid smoke: reloaded embedded session");
            var reloaded = secondHost.Surface!;
            Check(!ReferenceEquals(second, reloaded) && ReferenceEquals(secondHost.ViewModel, model), "Reload did not preserve the same model in a fresh session.");
            await UntilAsync("the reloaded web body input to show the shared draft", async () => await reloaded.ExecuteJavaScriptAsync("return document.querySelector('#body')?.value ?? '';") == "One shared draft");
            Check(ReferenceEquals(regions.Main.CurrentEntry, entry), "Reload changed navigation entry.");

            await regions.Main.BackAsync();
            await ReleasedAsync(secondHost, "editor unload");
            Console.WriteLine("Hybrid smoke: editor unloaded and released web session");
            await context.InvokeAsync(() => Check(regions.Main.Current is NotesListViewModel, "Presentation unload disposed the borrowed context or navigator."));

            // Closing the shell asks the editor's departure guard through the native dialog, as Back does.
            await regions.Main.PushAsync<EditorViewModel, int>(1);
            var dirty = (EditorViewModel)regions.Main.Current!;
            await context.InvokeAsync(() => dirty.Title = "Unsaved on close");
            window.Close();
            var keep = await QuestionAsync(regions, "the first shell close");
            Check(window.IsLoaded, "The shell closed before the departure guard answered.");
            await context.InvokeAsync(() => keep.NoCommand.Execute(null));
            await UntilAsync("the kept draft: no dialog and Main not transitioning", () => regions.Dialog.Current is null && !regions.Main.IsTransitioning,
                () => Describe(window, regions));
            Check(window.IsLoaded && ReferenceEquals(regions.Main.Current, dirty) && dirty.IsDirty, "Keep editing lost the shell or the draft.");
            window.Close();
            var discard = await QuestionAsync(regions, "the second shell close", () => Describe(window, regions));
            await context.InvokeAsync(() => discard.YesCommand.Execute(null));
            await UntilAsync("the shell to close after the discard", () => !window.IsLoaded, () => Describe(window, regions));
            Check(regions.Main.CurrentEntry is null && store.Get(1).Title == "Saved from web", "Closing the shell did not clear the region or kept the discarded draft.");
            Console.WriteLine("Hybrid smoke: shell close asked the guard, kept then discarded the draft");
        }
        finally
        {
            if (window.IsLoaded)
            {
                ((NavigationHost)window.FindName("Main")).GuardWindowClose = false;
                window.Close();
            }
        }
        Console.WriteLine("Hybrid smoke: shell closed, disposing application scope");
    }

    private static void Click(EditorView page, string button) =>
        ((Button)page.FindName(button)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task<RunicViewHost> OpenedAsync(EditorView page, string phase)
    {
        var host = page.WebHost ?? throw new InvalidOperationException($"No web host during {phase}.");
        await UntilAsync($"the web host to start opening during {phase}", () => host.State is not (RunicViewHostState.Closed or RunicViewHostState.Opening),
            () => $"host state: {host.State}");
        await WithinAsync(host.WaitForPresentationAsync(), TimeSpan.FromSeconds(30), $"the web host's presentation during {phase}");
        if (host.State != RunicViewHostState.Open)
            throw new InvalidOperationException($"Hybrid smoke failed during {phase}: {host.State}.", host.Error);
        return host;
    }

    // Leaving the tree closes the host's session, then its Desktop host, then its native child.
    private static async Task ReleasedAsync(RunicViewHost host, string phase)
    {
        await UntilAsync($"the web host to unload after {phase}", () => !host.IsLoaded, () => $"host state: {host.State}");
        await WithinAsync(host.WaitForPresentationAsync(), TimeSpan.FromSeconds(30), $"the web host's release after {phase}");
        Check(host.State == RunicViewHostState.Closed && host.Surface is null && host.Child is null, $"The web host was not released after {phase}.");
    }

    private static async Task<ConfirmViewModel> QuestionAsync(AppRegions regions, string close, Func<string>? state = null)
    {
        await UntilAsync($"the departure question of {close}", () => regions.Dialog.Current is ConfirmViewModel, state);
        return (ConfirmViewModel)regions.Dialog.Current!;
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

    private static string Describe(Window window, AppRegions regions) =>
        $"shell loaded: {window.IsLoaded}, dialog: {regions.Dialog.Current?.GetType().Name ?? "none"}, main: {regions.Main.Current?.GetType().Name ?? "none"}, "
        + $"main transitioning: {regions.Main.IsTransitioning}, main can go back: {regions.Main.CanGoBack}";

    private static Task UntilAsync(string what, Func<bool> ready, Func<string>? state = null) =>
        UntilAsync(what, () => Task.FromResult(ready()), state);

    // A timeout names the awaited condition and, when given, the state it last saw.
    private static async Task UntilAsync(string what, Func<Task<bool>> ready, Func<string>? state = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await ready())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException($"Hybrid smoke timed out after {elapsed.Elapsed.TotalSeconds:0} s waiting for {what}."
                    + (state is null ? "" : $" State: {state()}."));
            await Task.Delay(20);
        }
    }

    // Only the limit's own timeout is renamed; a timeout of a wait inside the task already names its condition.
    private static async Task WithinAsync(Task task, TimeSpan limit, string what)
    {
        try { await task.WaitAsync(limit); }
        catch (TimeoutException error) when (!task.IsCompleted) { throw new TimeoutException($"Hybrid smoke timed out after {limit.TotalSeconds:0} s waiting for {what}.", error); }
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
