using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.DependencyInjection;
using NotesWindowViews;
using Runic.Application.Testing;
using Runic.Application.Views;
using Xunit;

namespace NotesViewFirst.Tests;

// Drives the real ViewModels and generated Bridges of one notes window, as the
// browser would, without a browser or native window.
public sealed class NotesWindowTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new();
    private readonly ServiceProvider _services;
    private readonly AsyncServiceScope _scope;
    private readonly RunicWindowTestHost<ShellViewModel> _host;

    public NotesWindowTests()
    {
        // The application's own registrations, with the test clock in place of the system clock.
        _services = new ServiceCollection()
            .AddSingleton<TimeProvider>(_clock)
            .AddNotes()
            .AddRunicViews()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _services.CreateAsyncScope();
        var window = _scope.ServiceProvider;
        _host = new RunicWindowTestHost<ShellViewModel>(
            window.GetRequiredService<ShellViewModel>(),
            window.GetRequiredService<Func<IBridgeTransport, WindowContentSession, ShellViewModel, IDisposable>>(),
            new RunicWindowTestHostOptions
            {
                ViewLocator = window.GetRequiredService<IRunicViewLocator>(),
                // The window graph shares the navigator's model context.
                ModelContext = window.GetRequiredService<IRunicModelContext>(),
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
        // Save declares SaveFailure, so the client receives the typed failure.
        Assert.Equal("domain-failed", reply.ErrorKind);
        Assert.Equal("""{"$case":"titleRequired"}""", reply.Failure?.GetRawText());
        Assert.Empty(_scope.ServiceProvider.GetRequiredService<NotesLibrary>().Notes);
    }

    [Fact]
    public async Task A_note_cannot_take_the_title_of_another_note()
    {
        var editor = await OpenEditorAsync();
        _scope.ServiceProvider.GetRequiredService<NotesLibrary>().Record("Groceries", "Milk");
        editor.Set(vm => vm.Title, "Groceries").EnsureOk();

        var reply = await editor.ExecuteAsync(vm => vm.SaveCommand);
        Assert.Equal("domain-failed", reply.ErrorKind);
        Assert.Equal("""{"$case":"titleTaken","existingTitle":"Groceries"}""", reply.Failure?.GetRawText());

        // The operation path reports the same failure.
        var status = await editor.Start(vm => vm.SaveCommand).WaitAsync();
        Assert.Equal("domain-failed", status.Kind);
        Assert.Equal("titleTaken", status.Failure?.GetProperty("$case").GetString());
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

        // The Back waits in the document's guard until the dialog answers.
        var leaving = sidebar.Start(vm => vm.OpenHomeCommand);
        await WhenAsync(Navigation.Dialog, () => Navigation.Dialog.Current is not null);
        var dialog = _host.Root.View<ConfirmNavigationViewModel>(vm => vm.Dialog);
        Assert.Equal("Discard the unsaved edits and return Home?", dialog.Snapshot().Read(vm => vm.Message));

        (await dialog.ExecuteAsync(vm => vm.ConfirmCommand)).EnsureOk();
        Assert.Equal("succeeded", (await leaving.WaitAsync()).Kind);
        var shell = _host.Root.Snapshot();
        Assert.Null(shell.Reference(vm => vm.Dialog));
        Assert.Equal("home", shell.Reference(vm => vm.Main)?.Kind);
        Assert.False(Editor.IsDirty);
        Assert.Equal("Changes discarded.", Editor.SavedMessage);
    }

    // #61: reject a Back through an asynchronous guard; current content, entry
    // identity and history are unchanged.
    [Fact]
    public async Task Cancelling_the_dialog_keeps_the_document_entry_and_its_draft()
    {
        var editor = await OpenEditorAsync();
        editor.Set(vm => vm.Title, "Draft").EnsureOk();
        var entry = Navigation.Main.CurrentEntry!;
        var history = Navigation.Main.History.ToArray();

        var leaving = _host.Root.View<SidebarViewModel>(vm => vm.Sidebar).Start(vm => vm.OpenHomeCommand);
        await WhenAsync(Navigation.Dialog, () => Navigation.Dialog.Current is not null);
        (await _host.Root.View<ConfirmNavigationViewModel>(vm => vm.Dialog).ExecuteAsync(vm => vm.CancelCommand)).EnsureOk();
        Assert.Equal("succeeded", (await leaving.WaitAsync()).Kind);

        Assert.Same(entry, Navigation.Main.CurrentEntry);
        Assert.Equal(NavigationEntryState.Active, entry.State);
        Assert.Equal(history, Navigation.Main.History);
        Assert.Null(Navigation.Dialog.Current);
        Assert.Null(_host.Root.Snapshot().Reference(vm => vm.Dialog));
        Assert.Equal("document", _host.Root.Snapshot().Reference(vm => vm.Main)?.Kind);
        Assert.Equal("Draft", _host.Root.View<DocumentViewModel>(vm => vm.Main).View<EditorViewModel>(vm => vm.CurrentPane)
            .Snapshot().Read(vm => vm.Title));
    }

    // #61: enter the editor, change the draft, go to the preview and back: the
    // same retained editor entry resumes with its draft.
    [Fact]
    public async Task The_preview_round_trip_resumes_the_same_editor_entry()
    {
        var editor = await OpenEditorAsync();
        editor.Set(vm => vm.Body, "Milk").EnsureOk();
        var document = (DocumentViewModel)Navigation.Main.Current!;
        var editorEntry = document.CurrentPane.CurrentEntry!;
        var documentView = _host.Root.View<DocumentViewModel>(vm => vm.Main);

        (await documentView.ExecuteAsync(vm => vm.ShowPreviewCommand)).EnsureOk();
        Assert.Equal(DocumentPane.Preview, documentView.Snapshot().Read(vm => vm.ActivePane));
        Assert.Equal(NavigationEntryState.Retained, editorEntry.State);
        Assert.Equal("Milk", documentView.View<PreviewViewModel>(vm => vm.CurrentPane).Snapshot().Read(vm => vm.Excerpt));

        (await documentView.ExecuteAsync(vm => vm.ShowEditorCommand)).EnsureOk();
        Assert.Same(editorEntry, document.CurrentPane.CurrentEntry);
        Assert.Equal(NavigationEntryState.Active, editorEntry.State);
        Assert.Equal(DocumentPane.Editor, documentView.Snapshot().Read(vm => vm.ActivePane));
        Assert.Equal("Milk", documentView.View<EditorViewModel>(vm => vm.CurrentPane).Snapshot().Read(vm => vm.Body));
    }

    // #61: retiring the parent retires its owned child region once; the
    // borrowed editor and preview stay usable, and the next visit is a new entry.
    [Fact]
    public async Task Leaving_the_document_retires_it_and_its_pane_but_not_the_borrowed_editor()
    {
        await OpenEditorAsync();
        var first = (DocumentViewModel)Navigation.Main.Current!;
        var firstEntry = Navigation.Main.CurrentEntry!;
        (await _host.Root.View<DocumentViewModel>(vm => vm.Main).ExecuteAsync(vm => vm.ShowPreviewCommand)).EnsureOk();
        var paneEntry = first.CurrentPane.CurrentEntry!;

        (await _host.Root.View<SidebarViewModel>(vm => vm.Sidebar).ExecuteAsync(vm => vm.OpenHomeCommand)).EnsureOk();
        Assert.Equal(NavigationEntryState.Retired, firstEntry.State);
        Assert.Equal(NavigationEntryState.Retired, paneEntry.State);
        Assert.Null(first.CurrentPane.Current);
        Assert.Equal(1, Navigator.UnretiredEntryCount());
        Assert.Equal("Home", _host.Root.View<SidebarViewModel>(vm => vm.Sidebar).Snapshot().Read(vm => vm.Selected));

        var editor = await OpenEditorAsync();
        Assert.NotSame(first, Navigation.Main.Current);
        Assert.NotEqual(firstEntry.Id, Navigation.Main.CurrentEntry!.Id);
        // The window-scoped editor is borrowed: the draft survives the visit.
        Assert.Same(Editor, ((DocumentViewModel)Navigation.Main.Current!).CurrentPane.Current);
        editor.Set(vm => vm.Title, "Still here").EnsureOk();
        Assert.Equal("Still here", _scope.ServiceProvider.GetRequiredService<PreviewViewModel>().Heading);
    }

    // #61: racing requests. A second Home click supersedes the first: its
    // dialog is dismissed and only the second asks. Opening the notes twice
    // creates one document.
    [Fact]
    public async Task Racing_navigation_has_one_winner()
    {
        var sidebar = _host.Root.View<SidebarViewModel>(vm => vm.Sidebar);
        var notes = Navigation.OpenNotesAsync();
        await Task.WhenAll(notes, Navigation.OpenNotesAsync());
        // Home, one document and its editor pane.
        Assert.Equal(3, Navigator.UnretiredEntryCount());
        Assert.IsType<DocumentViewModel>(Navigation.Main.Current);
        Assert.Single(Navigation.Main.History);

        Editor.Title = "Draft";
        var first = Navigation.OpenHomeAsync();
        await WhenAsync(Navigation.Dialog, () => Navigation.Dialog.Current is not null);
        var firstDialog = Navigation.Dialog.CurrentEntry!;
        var second = Navigation.OpenHomeAsync();
        await first;
        await WhenAsync(Navigation.Dialog, () => Navigation.Dialog.CurrentEntry is { } entry && entry != firstDialog);
        Assert.Equal(NavigationEntryState.Retired, firstDialog.State);
        Assert.IsType<DocumentViewModel>(Navigation.Main.Current);

        (await _host.Root.View<ConfirmNavigationViewModel>(vm => vm.Dialog).ExecuteAsync(vm => vm.ConfirmCommand)).EnsureOk();
        await second;
        Assert.IsType<HomeViewModel>(Navigation.Main.Current);
        Assert.Null(Navigation.Dialog.Current);
        Assert.Equal("Home", sidebar.Snapshot().Read(vm => vm.Selected));
    }

    // Closing the window while the dialog asks dismisses it and retires every entry.
    [Fact]
    public async Task Closing_the_window_while_the_dialog_asks_retires_everything()
    {
        await OpenEditorAsync();
        Editor.Title = "Draft";
        var leaving = Navigation.OpenHomeAsync();
        await WhenAsync(Navigation.Dialog, () => Navigation.Dialog.Current is not null);

        await Navigator.DisposeAsync();
        await leaving;
        Assert.Equal(0, Navigator.UnretiredEntryCount());
        Assert.True(Editor.IsDirty);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _host.Dispose();
        // The window scope disposes its navigator, which retires the entries it owns.
        await _scope.DisposeAsync();
        await _services.DisposeAsync();
    }

    private WorkspaceNavigation Navigation => _scope.ServiceProvider.GetRequiredService<WorkspaceNavigation>();
    private RunicNavigator Navigator => _scope.ServiceProvider.GetRequiredService<RunicNavigator>();
    private EditorViewModel Editor => _scope.ServiceProvider.GetRequiredService<EditorViewModel>();

    // Completes when the condition holds, checked after each change the region raises.
    private static async Task WhenAsync<T>(NavigationRegion<T> region, Func<bool> condition) where T : class
    {
        var met = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (condition()) met.TrySetResult();
        }
        region.PropertyChanged += Check;
        try
        {
            if (condition()) return;
            await met.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            region.PropertyChanged -= Check;
        }
    }

    private async Task<RunicViewDriver<EditorViewModel>> OpenEditorAsync()
    {
        var sidebar = _host.Root.View<SidebarViewModel>(vm => vm.Sidebar);
        (await sidebar.ExecuteAsync(vm => vm.OpenNotesCommand)).EnsureOk();
        return _host.Root.View<DocumentViewModel>(vm => vm.Main).View<EditorViewModel>(vm => vm.CurrentPane);
    }
}
