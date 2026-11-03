using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// B6 and E2: IRunicWindowService lists, finds, activates and opens Windows for a result, with owners and modality, on
// the window-level test host (RunicWindowTestHost.CreateWindowAsync), which runs the real Windows' close decisions.
internal sealed class WindowManagementTests
{
    private static ServiceProvider Services(ILoggerFactory? loggers = null)
    {
        var services = new ServiceCollection();
        services.AddRunicReactiveModelContext();
        if (loggers is not null) services.AddSingleton(loggers);
        services.AddScoped<ManagedShellViewModel>();
        services.AddScoped<ManagedDocumentViewModel>();
        services.AddScoped<AccountPickerViewModel>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, ManagedShellViewModel, IDisposable>>(
            _ => (transport, content, vm) => new ManagedShellBridge(transport, vm, content: content));
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, ManagedDocumentViewModel, IDisposable>>(
            _ => (transport, content, vm) => new ManagedDocumentBridge(transport, vm, content: content));
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, AccountPickerViewModel, IDisposable>>(
            _ => (transport, content, vm) => new AccountPickerBridge(transport, vm, content: content));
        services.AddRunicWindowService();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Test]
    public async Task FindOrOpenFocusesTheOpenDocumentAsync()
    {
        await using var provider = Services();
        await using var shell = await RunicWindowTestHost.CreateWindowAsync<ManagedShellWindow>(provider);
        var windows = provider.GetRequiredService<IRunicWindowService>();
        var vm = (ManagedShellViewModel)shell.ViewModel;
        var changes = new List<(IRunicWindow Window, bool Opened)>();
        windows.WindowsChanged += (_, args) => { lock (changes) changes.Add((args.Window, args.Opened)); };

        var first = await vm.OpenDocumentAsync("a.txt");
        var again = await vm.OpenDocumentAsync("a.txt");
        var other = await vm.OpenDocumentAsync("b.txt");
        Require(ReferenceEquals(first, again), "FindOrOpenAsync opened a second Window for the same key.");
        Require(!ReferenceEquals(first, other), "FindOrOpenAsync found a Window with another key.");
        Require(shell.OpenedWindows.Count == 2, $"The test host lists {shell.OpenedWindows.Count} opened Windows.");
        var opened = shell.OpenedWindows[0];
        Require(opened.Window == first && opened.Key as string == "a.txt" && opened.Parameter is ManagedDocument { Path: "a.txt" },
            "The opened Window does not expose its key and parameter.");
        Require(opened.Root<ManagedDocumentViewModel>() is not null, "The opened Window has no root driver.");
        Require(opened.Presentation.PresentCount == 1, $"The found Window was presented {opened.Presentation.PresentCount} times.");
        Require(windows.Windows.Count == 3 && windows.FindWindow<ManagedDocumentWindow>("b.txt") == other,
            "IRunicWindowService.Windows does not list every open Window.");
        Require(windows.FindWindow<AccountPickerWindow>("a.txt") is null, "A key matched a Window of another type.");

        // The user closes the document; the next request opens a new one.
        Require(await opened.RequestUserCloseAsync(), "The saved document did not close.");
        await WaitUntilAsync(() => windows.Windows.Count == 2, "The closed Window stayed in IRunicWindowService.Windows.");
        var reopened = await vm.OpenDocumentAsync("a.txt");
        Require(!ReferenceEquals(reopened, first) && shell.OpenedWindows.Count == 3, "A closed Window was found again.");
        lock (changes)
            Require(changes.Count(change => change.Opened) == 3 && changes.Count(change => !change.Opened) == 1,
                $"WindowsChanged reported {changes.Count} changes.");
    }

    [Test]
    public async Task ConcurrentRequestsOpenOneWindowAsync()
    {
        await using var provider = Services();
        await using var shell = await RunicWindowTestHost.CreateWindowAsync<ManagedShellWindow>(provider);
        var vm = (ManagedShellViewModel)shell.ViewModel;
        var windows = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => vm.OpenDocumentAsync("same.txt")));
        Require(windows.Distinct().Count() == 1 && shell.OpenedWindows.Count == 1, "Concurrent FindOrOpenAsync calls opened several Windows.");
    }

    [Test]
    public async Task TheCloseGuardDecidesAUserCloseAsync()
    {
        await using var provider = Services();
        await using var shell = await RunicWindowTestHost.CreateWindowAsync<ManagedShellWindow>(provider);
        var document = await ((ManagedShellViewModel)shell.ViewModel).OpenDocumentAsync("draft.txt");
        var opened = shell.OpenedWindows.Single();
        document.ViewModel.Saved = false;
        Require(!await opened.RequestUserCloseAsync(), "A user close passed a guard that kept the Window.");
        Require(!await document.RequestCloseAsync(), "RequestCloseAsync passed a guard that kept the Window.");
        Require(opened.IsOpen, "The guarded Window closed.");
        document.ViewModel.Saved = true;
        Require(await document.RequestCloseAsync() && !opened.IsOpen, "The guard that allowed the close kept the Window open.");
        Require(await opened.RequestUserCloseAsync(), "A closed Window did not report closed.");
    }

    [Test]
    public async Task AModalWindowReturnsItsResultAsync()
    {
        await using var provider = Services();
        await using var shell = await RunicWindowTestHost.CreateWindowAsync<ManagedShellWindow>(provider);
        var windows = provider.GetRequiredService<IRunicWindowService>();
        var choosing = ((ManagedShellViewModel)shell.ViewModel).ChooseAccountAsync();
        var picker = await shell.WaitForOpenedWindowAsync<AccountPickerWindow>();
        Require(picker.Owner == shell && picker.Modality == RunicWindowModality.Window && shell.IsBlocked,
            "The picker is not window-modal to the shell.");
        Require(picker.Parameter is AccountQuery { Prompt: "Pay from" }, "The picker did not receive its parameter.");

        // Activating the blocked shell brings the modal picker forward too.
        _ = await windows.ActivateAsync(shell.Window);
        Require(shell.Presentation.PresentCount == 1 && picker.Presentation.PresentCount == 1,
            "Activating the owner did not bring the modal Window forward.");

        Require(await ((AccountPickerViewModel)picker.ViewModel).ChooseAsync("savings"), "The first result was refused.");
        var chosen = await choosing.WaitAsync(TimeSpan.FromSeconds(5));
        Require(chosen.HasValue && chosen.Value == "savings", $"The opener received {chosen}.");
        Require(!picker.IsOpen && !shell.IsBlocked, "Completing the picker did not close it.");
        Require(!await ((AccountPickerViewModel)picker.ViewModel).ChooseAsync("other"), "A second result was accepted.");
    }

    [Test]
    public async Task ClosingWithoutAResultReturnsNoneAsync()
    {
        await using var provider = Services();
        await using var shell = await RunicWindowTestHost.CreateWindowAsync<ManagedShellWindow>(provider);
        var vm = (ManagedShellViewModel)shell.ViewModel;

        // The user closes the picker.
        var choosing = vm.ChooseAccountAsync();
        var picker = await shell.WaitForOpenedWindowAsync<AccountPickerWindow>();
        Require(await picker.RequestUserCloseAsync(), "The picker did not close.");
        var none = await choosing.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!none.HasValue && none == default && none.GetValueOrDefault("fallback") == "fallback", $"A user close returned {none}.");
        Require(Throws<InvalidOperationException>(() => _ = none.Value), "Value of no result did not throw.");

        // Cancelling the caller closes the picker without asking.
        using var cancel = new CancellationTokenSource();
        choosing = vm.ChooseAccountAsync(cancel.Token);
        picker = await shell.WaitForOpenedWindowAsync<AccountPickerWindow>();
        await cancel.CancelAsync();
        var cancelled = false;
        try { await choosing.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled && !picker.IsOpen, "Cancelling OpenForResultAsync did not close the Window and throw.");
    }

    [Test]
    public async Task ClosingTheOwnerClosesItsWindowsAsync()
    {
        await using var provider = Services();
        await using var shell = await RunicWindowTestHost.CreateWindowAsync<ManagedShellWindow>(provider);
        var choosing = ((ManagedShellViewModel)shell.ViewModel).ChooseAccountAsync();
        var picker = await shell.WaitForOpenedWindowAsync<AccountPickerWindow>();
        Require(await shell.RequestUserCloseAsync(), "The shell did not close.");
        var result = await choosing.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!result.HasValue && !picker.IsOpen, "The owned picker outlived its owner, or returned a result.");
    }

    [Test]
    public async Task ResultAndModalityAreCheckedAsync()
    {
        await using var provider = Services();
        await using var shell = await RunicWindowTestHost.CreateWindowAsync<ManagedShellWindow>(provider);
        var windows = provider.GetRequiredService<IRunicWindowService>();
        // A modal Window needs an owner.
        var error = await Catch<ArgumentException>(() => windows.OpenWindowAsync<ManagedDocumentWindow, ManagedDocument>(new("x"),
            new RunicWindowOptions { Modality = RunicWindowModality.Window }).AsTask());
        Require(error is not null, "A window-modal Window without an owner opened.");
        // A Window opened without a result cannot complete one.
        var document = await windows.OpenWindowAsync<ManagedDocumentWindow, ManagedDocument>(new("plain"));
        var result = document.Services.GetRequiredService<IRunicWindowResult<string>>();
        var refused = await Catch<InvalidOperationException>(() => result.CompleteAsync("x").AsTask());
        Require(refused?.Message.Contains("not opened for a result", StringComparison.Ordinal) == true, $"Completing a plain Window: {refused}");
        // The scope names its Window for an owned open.
        Require(shell.Services.GetRequiredService<IRunicWindowContext>().Window == shell.Window, "IRunicWindowContext.Window is not the Window.");
    }

    [Test]
    public async Task ObserveWindowsReportsTheListAsync()
    {
        await using var provider = Services();
        await using var shell = await RunicWindowTestHost.CreateWindowAsync<ManagedShellWindow>(provider);
        var windows = provider.GetRequiredService<IRunicWindowService>();
        var counts = new List<int>();
        using (windows.ObserveWindows().Subscribe(new Observer(list => { lock (counts) counts.Add(list.Count); })))
        {
            var document = await ((ManagedShellViewModel)shell.ViewModel).OpenDocumentAsync("seen.txt");
            await document.CloseAsync(TimeSpan.Zero);
            await WaitUntilAsync(() => { lock (counts) return counts.Count == 3; }, "ObserveWindows did not report the close.");
        }
        lock (counts) Require(counts.SequenceEqual([1, 2, 1]), $"ObserveWindows reported {string.Join(", ", counts)}.");
    }

    private sealed class Observer(Action<IReadOnlyList<IRunicWindow>> next) : IObserver<IReadOnlyList<IRunicWindow>>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(IReadOnlyList<IRunicWindow> value) => next(value);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++) await Task.Delay(10);
        Require(condition(), message);
    }

    private static async Task<TException?> Catch<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException error) { return error; }
        return null;
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return true; }
        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
