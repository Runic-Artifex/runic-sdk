using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.DependencyInjection;
using NotesWindowViews;
using Runic.Application.Testing;
using Runic.Application.Views;
using Xunit;

namespace NotesViewFirst.Tests;

// Drives the real ViewModels and generated Bridges of one notes window, as the
// browser would, without a browser or native window.
public sealed class NotesWindowTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new();
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;
    private readonly RunicWindowTestHost<ShellViewModel> _host;

    public NotesWindowTests()
    {
        // The application's own registrations, with the test clock in place of the system clock.
        _services = new ServiceCollection()
            .AddSingleton<TimeProvider>(_clock)
            .AddNotes()
            .AddRunicViews()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _services.CreateScope();
        var window = _scope.ServiceProvider;
        _host = new RunicWindowTestHost<ShellViewModel>(
            window.GetRequiredService<ShellViewModel>(),
            window.GetRequiredService<Func<IBridgeTransport, WindowContentSession, ShellViewModel, IDisposable>>(),
            new RunicWindowTestHostOptions
            {
                ViewLocator = window.GetRequiredService<IRunicViewLocator>(),
                TimeProvider = _clock,
            });
    }

    [Fact]
    public void The_window_opens_on_home_with_stable_content_routes()
    {
        var shell = _host.Root.Snapshot();

        // The test host numbers content in presentation order.
        Assert.Equal(new PageReference("sidebar", "1"), shell.Reference(vm => vm.Sidebar));
        Assert.Equal(new PageReference("home", "2"), shell.Reference(vm => vm.Main));
        Assert.Null(shell.Reference(vm => vm.Dialog));
        var home = _host.Root.View<HomeViewModel>(vm => vm.Main).Snapshot();
        Assert.Equal("Welcome to composed Notes", home.Read(vm => vm.Greeting));
        Assert.Empty(home.Keys(vm => vm.RecentNotes));
    }

    [Fact]
    public async Task Saving_waits_for_storage_and_lists_the_note_on_home()
    {
        var editor = await OpenEditorAsync();
        editor.Set(vm => vm.Title, "Groceries").EnsureOk();
        editor.Set(vm => vm.Body, "Milk, eggs").EnsureOk();

        var save = editor.Start(vm => vm.SaveCommand);
        Assert.Equal("accepted", save.Admission);
        Assert.True(editor.Snapshot().IsExecuting(vm => vm.SaveCommand));
        Assert.Equal("running", save.Status().Kind);

        // Storage takes 250 ms of the test clock.
        _clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal("succeeded", (await save.WaitAsync()).Kind);
        var saved = editor.Snapshot();
        Assert.Equal("Saved Groceries", saved.Read(vm => vm.SavedMessage));
        Assert.False(saved.Read(vm => vm.IsDirty));

        var sidebar = _host.Root.View<SidebarViewModel>(vm => vm.Sidebar);
        (await sidebar.ExecuteAsync(vm => vm.OpenHomeCommand)).EnsureOk();
        var home = _host.Root.View<HomeViewModel>(vm => vm.Main).Snapshot();
        Assert.Equal(["Groceries"], home.Keys(vm => vm.RecentNotes));
    }

    [Fact]
    public async Task A_note_without_a_title_is_not_saved()
    {
        var editor = await OpenEditorAsync();
        editor.Set(vm => vm.Title, " ").EnsureOk();

        var reply = await editor.ExecuteAsync(vm => vm.SaveCommand);

        Assert.False(reply.Ok);
        // The ViewModel throws ArgumentException, which the Bridge reports as a rejected call.
        Assert.Equal("rejected", reply.ErrorKind);
        Assert.Empty(_scope.ServiceProvider.GetRequiredService<NotesLibrary>().Notes);
    }

    [Fact]
    public async Task Saved_notes_reach_home_as_keyed_collection_changes()
    {
        var home = _host.Root.View<HomeViewModel>(vm => vm.Main);
        var tracker = home.Track();
        var library = _scope.ServiceProvider.GetRequiredService<NotesLibrary>();

        library.Record("Groceries", "Milk");
        library.Record("Ideas", "A notes app");
        await tracker.WaitUntilAsync(state => state.Keys(vm => vm.RecentNotes).Count == 2);
        // Saving an older note again replaces its row and moves it to the top.
        library.Record("Groceries", "Milk, eggs");
        var state = await tracker.WaitUntilAsync(state => state.Keys(vm => vm.RecentNotes)[0] == "Groceries");

        Assert.Equal(["Groceries", "Ideas"], state.Keys(vm => vm.RecentNotes));
        Assert.Equal(0, tracker.FullStates);
        Assert.Contains(tracker.Changes, change => change.Kind == "replace" && change.Keys.SequenceEqual(["Groceries"]));
        Assert.Contains(tracker.Changes, change => change.Kind == "move" && change.Index == 0);
        // The applied frames match the state .NET holds.
        tracker.Verify();
    }

    [Fact]
    public async Task Leaving_unsaved_edits_asks_for_confirmation()
    {
        var editor = await OpenEditorAsync();
        editor.Set(vm => vm.Title, "Draft").EnsureOk();
        var sidebar = _host.Root.View<SidebarViewModel>(vm => vm.Sidebar);

        (await sidebar.ExecuteAsync(vm => vm.OpenHomeCommand)).EnsureOk();
        var dialog = _host.Root.View<ConfirmNavigationViewModel>(vm => vm.Dialog);
        Assert.Equal("Discard the unsaved edits and return Home?", dialog.Snapshot().Read(vm => vm.Message));

        (await dialog.ExecuteAsync(vm => vm.ConfirmCommand)).EnsureOk();
        var shell = _host.Root.Snapshot();
        Assert.Null(shell.Reference(vm => vm.Dialog));
        Assert.Equal("home", shell.Reference(vm => vm.Main)?.Kind);
    }

    public void Dispose()
    {
        _host.Dispose();
        _scope.Dispose();
        _services.Dispose();
    }

    private async Task<RunicViewDriver<EditorViewModel>> OpenEditorAsync()
    {
        var sidebar = _host.Root.View<SidebarViewModel>(vm => vm.Sidebar);
        (await sidebar.ExecuteAsync(vm => vm.OpenNotesCommand)).EnsureOk();
        return _host.Root.View<DocumentViewModel>(vm => vm.Main).View<EditorViewModel>(vm => vm.CurrentPane);
    }
}
