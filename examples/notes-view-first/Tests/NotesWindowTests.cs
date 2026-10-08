using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.DependencyInjection;
using NotesWindowViews;
using Runic.Application.Testing;
using Runic.Application.Views;
using Xunit;
using Runic.Navigation;

namespace NotesViewFirst.Tests;

// Drives the real ViewModels and generated Bridges of one notes window, as the
// browser would, without a browser or native window.
public sealed class NotesWindowTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new();
    private readonly TestWindow _window;

    public NotesWindowTests() => _window = new TestWindow(_clock);

    private AsyncServiceScope Scope => _window.Scope;
    private RunicWindowTestHost<ShellViewModel> Host => _window.Host;

    [Fact]
    public void The_window_opens_on_home_with_stable_content_routes()
    {
        var shell = Host.Root.Snapshot();

        // The test host numbers content in presentation order.
        Assert.Equal(new PageReference("sidebar", "1"), shell.Reference(vm => vm.Sidebar));
        Assert.Equal(new PageReference("home", "2"), shell.Reference(vm => vm.Main));
        Assert.Null(shell.Reference(vm => vm.Dialog));
        var home = Host.Root.View<HomeViewModel>(vm => vm.Main).Snapshot();
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

        var sidebar = Host.Root.View<SidebarViewModel>(vm => vm.Sidebar);
        (await sidebar.ExecuteAsync(vm => vm.OpenHomeCommand)).EnsureOk();
        var home = Host.Root.View<HomeViewModel>(vm => vm.Main).Snapshot();
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
        Assert.Empty(Scope.ServiceProvider.GetRequiredService<NotesLibrary>().Notes);
    }

    [Fact]
    public async Task A_note_cannot_take_the_title_of_another_note()
    {
        var editor = await OpenEditorAsync();
        Scope.ServiceProvider.GetRequiredService<NotesLibrary>().Record("Groceries", "Milk");
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
        var home = Host.Root.View<HomeViewModel>(vm => vm.Main);
        var tracker = home.Track();
        var library = Scope.ServiceProvider.GetRequiredService<NotesLibrary>();

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
        var sidebar = Host.Root.View<SidebarViewModel>(vm => vm.Sidebar);

        // The Back waits in the document's guard until the dialog answers.
        var leaving = sidebar.Start(vm => vm.OpenHomeCommand);
        await WhenAsync(Navigation.Dialog, () => Navigation.Dialog.Current is not null);
        var dialog = Host.Root.View<ConfirmNavigationViewModel>(vm => vm.Dialog);
        Assert.Equal("Discard the unsaved edits and return Home?", dialog.Snapshot().Read(vm => vm.Message));

        (await dialog.ExecuteAsync(vm => vm.ConfirmCommand)).EnsureOk();
        Assert.Equal("succeeded", (await leaving.WaitAsync()).Kind);
        var shell = Host.Root.Snapshot();
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

        var leaving = Host.Root.View<SidebarViewModel>(vm => vm.Sidebar).Start(vm => vm.OpenHomeCommand);
        await WhenAsync(Navigation.Dialog, () => Navigation.Dialog.Current is not null);
        (await Host.Root.View<ConfirmNavigationViewModel>(vm => vm.Dialog).ExecuteAsync(vm => vm.CancelCommand)).EnsureOk();
        Assert.Equal("succeeded", (await leaving.WaitAsync()).Kind);

        Assert.Same(entry, Navigation.Main.CurrentEntry);
        Assert.Equal(NavigationEntryState.Active, entry.State);
        Assert.Equal(history, Navigation.Main.History);
        Assert.Null(Navigation.Dialog.Current);
        Assert.Null(Host.Root.Snapshot().Reference(vm => vm.Dialog));
        Assert.Equal("document", Host.Root.Snapshot().Reference(vm => vm.Main)?.Kind);
        Assert.Equal("Draft", Host.Root.View<DocumentViewModel>(vm => vm.Main).View<EditorViewModel>(vm => vm.CurrentPane)
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
        var documentView = Host.Root.View<DocumentViewModel>(vm => vm.Main);

        (await documentView.ExecuteAsync(vm => vm.ShowPreviewCommand)).EnsureOk();
        Assert.Equal(DocumentPane.Preview, documentView.Snapshot().Read(vm => vm.ActivePane));
        Assert.Equal(NavigationEntryState.Retained, editorEntry.State);
        Assert.Equal("Milk", documentView.View<PreviewViewModel>(vm => vm.CurrentPane).Snapshot().Read(vm => vm.Excerpt));

        (await documentView.ExecuteAsync(vm => vm.ShowEditorCommand)).EnsureOk();
        Assert.Same(editorEntry, document.CurrentPane.CurrentEntry);
        Assert.Equal(NavigationEntryState.Active, editorEntry.State);
        Assert.Equal(DocumentPane.Editor, documentView.Snapshot().Read(vm => vm.ActivePane));
        Assert.Equal("Milk", documentView.View<EditorViewModel>(vm => vm.CurrentPane).Snapshot().Read(vm => vm.Body));
        // Resuming is not initializing: the document initialized once, and the editor entry is the same one.
        Assert.Equal(1, document.Initializations);
        Assert.Equal(editorEntry.Id, document.CurrentPane.CurrentEntry!.Id);
    }

    // #61: retiring the parent retires its owned child region once; the
    // borrowed editor and preview stay usable, and the next visit is a new entry.
    [Fact]
    public async Task Leaving_the_document_retires_it_and_its_pane_but_not_the_borrowed_editor()
    {
        await OpenEditorAsync();
        var first = (DocumentViewModel)Navigation.Main.Current!;
        var firstEntry = Navigation.Main.CurrentEntry!;
        (await Host.Root.View<DocumentViewModel>(vm => vm.Main).ExecuteAsync(vm => vm.ShowPreviewCommand)).EnsureOk();
        var paneEntry = first.CurrentPane.CurrentEntry!;

        (await Host.Root.View<SidebarViewModel>(vm => vm.Sidebar).ExecuteAsync(vm => vm.OpenHomeCommand)).EnsureOk();
        Assert.Equal(NavigationEntryState.Retired, firstEntry.State);
        Assert.Equal(NavigationEntryState.Retired, paneEntry.State);
        Assert.Null(first.CurrentPane.Current);
        Assert.Equal(1, Navigator.UnretiredEntryCount);
        Assert.Equal("Home", Host.Root.View<SidebarViewModel>(vm => vm.Sidebar).Snapshot().Read(vm => vm.Selected));

        var editor = await OpenEditorAsync();
        Assert.NotSame(first, Navigation.Main.Current);
        Assert.NotEqual(firstEntry.Id, Navigation.Main.CurrentEntry!.Id);
        // The window-scoped editor is borrowed: the draft survives the visit.
        Assert.Same(Editor, ((DocumentViewModel)Navigation.Main.Current!).CurrentPane.Current);
        editor.Set(vm => vm.Title, "Still here").EnsureOk();
        Assert.Equal("Still here", Scope.ServiceProvider.GetRequiredService<PreviewViewModel>().Heading);
    }

    // #61: racing requests. Opening the notes twice creates one document. While the
    // dialog asks, the window's navigation is modal: a second Home or Notes request
    // does nothing, and the one Back that asks decides.
    [Fact]
    public async Task Racing_navigation_has_one_winner()
    {
        var sidebar = Host.Root.View<SidebarViewModel>(vm => vm.Sidebar);
        var notes = Navigation.OpenNotesAsync();
        await Task.WhenAll(notes, Navigation.OpenNotesAsync());
        // Home, one document and its editor pane.
        Assert.Equal(3, Navigator.UnretiredEntryCount);
        Assert.IsType<DocumentViewModel>(Navigation.Main.Current);
        Assert.Single(Navigation.Main.History);

        await Scope.ServiceProvider.GetRequiredService<IRunicModelContext>().InvokeAsync(() => Editor.Title = "Draft");
        var leaving = Navigation.OpenHomeAsync();
        await WhenAsync(Navigation.Dialog, () => Navigation.Dialog.Current is not null);
        var dialog = Navigation.Dialog.CurrentEntry!;
        await Navigation.OpenHomeAsync();
        await Navigation.OpenNotesAsync();
        Assert.Same(dialog, Navigation.Dialog.CurrentEntry);
        Assert.False(leaving.IsCompleted);

        (await Host.Root.View<ConfirmNavigationViewModel>(vm => vm.Dialog).ExecuteAsync(vm => vm.ConfirmCommand)).EnsureOk();
        await leaving;
        Assert.IsType<HomeViewModel>(Navigation.Main.Current);
        Assert.Null(Navigation.Dialog.Current);
        Assert.Equal("Home", sidebar.Snapshot().Read(vm => vm.Selected));
        Assert.Equal(1, Navigator.UnretiredEntryCount);
    }

    // The confirm is modal: the sidebar and the document's pane commands report that they
    // are unavailable while it asks, and the Bridge rejects them, so a pane change can't
    // supersede the Back and dismiss the dialog.
    [Fact]
    public async Task Navigation_commands_are_unavailable_while_the_dialog_asks()
    {
        var editor = await OpenEditorAsync();
        editor.Set(vm => vm.Title, "Draft").EnsureOk();
        var sidebar = Host.Root.View<SidebarViewModel>(vm => vm.Sidebar);
        var documentView = Host.Root.View<DocumentViewModel>(vm => vm.Main);
        Assert.True(sidebar.Snapshot().CanExecute(vm => vm.OpenHomeCommand));
        Assert.True(documentView.Snapshot().CanExecute(vm => vm.ShowPreviewCommand));

        var leaving = sidebar.Start(vm => vm.OpenHomeCommand);
        await WhenAsync(Navigation.Dialog, () => Navigation.Dialog.Current is not null);
        var sidebarState = sidebar.Snapshot();
        Assert.False(sidebarState.CanExecute(vm => vm.OpenHomeCommand));
        Assert.False(sidebarState.CanExecute(vm => vm.OpenNotesCommand));
        var documentState = documentView.Snapshot();
        Assert.False(documentState.CanExecute(vm => vm.ShowEditorCommand));
        Assert.False(documentState.CanExecute(vm => vm.ShowPreviewCommand));
        Assert.Equal("rejected", (await documentView.ExecuteAsync(vm => vm.ShowPreviewCommand)).ErrorKind);
        Assert.Equal("rejected", (await sidebar.ExecuteAsync(vm => vm.OpenNotesCommand)).ErrorKind);
        Assert.NotNull(Navigation.Dialog.Current);
        Assert.Equal(DocumentPane.Editor, documentView.Snapshot().Read(vm => vm.ActivePane));

        (await Host.Root.View<ConfirmNavigationViewModel>(vm => vm.Dialog).ExecuteAsync(vm => vm.CancelCommand)).EnsureOk();
        Assert.Equal("succeeded", (await leaving.WaitAsync()).Kind);
        Assert.True(sidebar.Snapshot().CanExecute(vm => vm.OpenHomeCommand));
        Assert.True(documentView.Snapshot().CanExecute(vm => vm.ShowPreviewCommand));
    }

    // Cancel always ends the question, also when its answer can't commit. Here another
    // dialog covers the confirm, so the confirm's Back is rejected as NotCurrent; Cancel
    // then dismisses the request, and the guard keeps the document.
    [Fact]
    public async Task Cancel_ends_the_question_when_its_answer_cannot_commit()
    {
        var editor = await OpenEditorAsync();
        editor.Set(vm => vm.Title, "Draft").EnsureOk();
        var leaving = Navigation.OpenHomeAsync();
        await WhenAsync(Navigation.Dialog, () => Navigation.Dialog.Current is not null);
        var confirm = (ConfirmNavigationViewModel)Navigation.Dialog.Current!;
        var cover = new ConfirmNavigationViewModel("Covering");
        Assert.IsType<NavigationResult<IDialogViewModel>.Committed>(
            await Navigation.Dialog.PushAsync(NavigationTarget.Own<IDialogViewModel>(cover)));

        await confirm.CancelCommand.ExecuteAsync(null);
        await leaving.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<DocumentViewModel>(Navigation.Main.Current);
        Assert.True(Editor.IsDirty);
        Assert.Equal("Draft", Editor.Title);

        // The dismissed confirm stays below the cover; when it returns, Cancel closes it.
        Assert.Same(cover, Navigation.Dialog.Current);
        await Navigation.Dialog.BackAsync();
        Assert.Same(confirm, Navigation.Dialog.Current);
        await confirm.CancelCommand.ExecuteAsync(null);
        Assert.Null(Navigation.Dialog.Current);
        Assert.True(Navigation.CanNavigate);
    }

    // A confirmed departure discards the draft in the turn that commits the Back, before the
    // regions raise their changes: a handler of Main's Current already sees the discarded draft.
    [Fact]
    public async Task A_confirmed_departure_discards_the_draft_in_the_commit_turn()
    {
        var editor = await OpenEditorAsync();
        editor.Set(vm => vm.Title, "Draft").EnsureOk();
        var draft = Editor;
        bool? dirtyWhenHomeShows = null;
        void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(NavigationRegion<IMainViewModel>.Current) && Navigation.Main.Current is HomeViewModel)
                dirtyWhenHomeShows ??= draft.IsDirty;
        }

        Navigation.Main.PropertyChanged += OnMainChanged;
        try
        {
            var leaving = Host.Root.View<SidebarViewModel>(vm => vm.Sidebar).Start(vm => vm.OpenHomeCommand);
            await WhenAsync(Navigation.Dialog, () => Navigation.Dialog.Current is not null);
            (await Host.Root.View<ConfirmNavigationViewModel>(vm => vm.Dialog).ExecuteAsync(vm => vm.ConfirmCommand)).EnsureOk();
            Assert.Equal("succeeded", (await leaving.WaitAsync()).Kind);
        }
        finally { Navigation.Main.PropertyChanged -= OnMainChanged; }
        Assert.IsType<HomeViewModel>(Navigation.Main.Current);
        Assert.False(dirtyWhenHomeShows);
        Assert.False(draft.IsDirty);
        Assert.Equal("Untitled", draft.Title);
    }

    // A confirmed guard whose Back is then superseded keeps the document and its draft. The
    // yes ends with the Back, and the discard runs only in a commit turn of a departure the
    // guard allowed: closing the window afterwards, which clears Main without asking guards,
    // does not discard the draft.
    [Fact]
    public async Task A_superseded_confirmed_back_keeps_the_draft_through_window_close()
    {
        var editor = await OpenEditorAsync();
        editor.Set(vm => vm.Title, "Draft").EnsureOk();
        var document = Navigation.Main.Current;
        var draft = Editor; // the window scope resolves nothing after it closes

        // When the confirmed dialog leaves, a request that bypasses CanNavigate supersedes the
        // Back after its guard said yes, and is cancelled before it asks the guard itself.
        var supersede = new CancellationTokenSource();
        var superseding = new TaskCompletionSource<NavigationResult<IMainViewModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Supersede(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(NavigationRegion<IDialogViewModel>.Current) || Navigation.Dialog.Current is not null) return;
            Navigation.Dialog.PropertyChanged -= Supersede;
            var push = Navigation.Main.PushAsync(NavigationTarget.Create<IMainViewModel>(
                _ => throw new InvalidOperationException("The superseding push must not prepare.")), cancellationToken: supersede.Token);
            supersede.Cancel();
            _ = push.AsTask().ContinueWith(task => superseding.SetResult(task.Result), TaskScheduler.Default);
        }

        var leaving = Host.Root.View<SidebarViewModel>(vm => vm.Sidebar).Start(vm => vm.OpenHomeCommand);
        await WhenAsync(Navigation.Dialog, () => Navigation.Dialog.Current is not null);
        Navigation.Dialog.PropertyChanged += Supersede;
        (await Host.Root.View<ConfirmNavigationViewModel>(vm => vm.Dialog).ExecuteAsync(vm => vm.ConfirmCommand)).EnsureOk();
        Assert.Equal("succeeded", (await leaving.WaitAsync()).Kind);
        Assert.IsType<NavigationResult<IMainViewModel>.Rejected>(await superseding.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(document, Navigation.Main.Current);
        Assert.True(Editor.IsDirty);

        await _window.DisposeAsync();
        Assert.Equal("Draft", draft.Title);
        Assert.True(draft.IsDirty);
    }

    // #61: a document that fails to prepare leaves Home current and nothing tracked, and
    // the next visit works. Notes has no reset path, so a guard-rejected reset has no Notes test.
    [Fact]
    public async Task A_document_that_fails_to_prepare_leaves_home_and_the_next_visit_works()
    {
        var attempts = 0;
        await using var window = new TestWindow(new FakeTimeProvider(), services => services.AddScoped(provider =>
            ++attempts == 1
                ? throw new InvalidOperationException("The preview is unavailable.")
                : new PreviewViewModel(provider.GetRequiredService<EditorViewModel>())));
        var navigation = window.Scope.ServiceProvider.GetRequiredService<WorkspaceNavigation>();
        var navigator = window.Scope.ServiceProvider.GetRequiredService<RunicNavigator>();
        var sidebar = window.Host.Root.View<SidebarViewModel>(vm => vm.Sidebar);

        (await sidebar.ExecuteAsync(vm => vm.OpenNotesCommand)).EnsureOk();
        Assert.IsType<HomeViewModel>(navigation.Main.Current);
        Assert.Equal(1, navigator.UnretiredEntryCount);
        Assert.Equal("home", window.Host.Root.Snapshot().Reference(vm => vm.Main)?.Kind);

        (await sidebar.ExecuteAsync(vm => vm.OpenNotesCommand)).EnsureOk();
        Assert.IsType<DocumentViewModel>(navigation.Main.Current);
        Assert.Equal(3, navigator.UnretiredEntryCount);
        Assert.Equal(2, attempts);
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
        Assert.Equal(0, Navigator.UnretiredEntryCount);
        Assert.True(Editor.IsDirty);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _window.DisposeAsync();

    private WorkspaceNavigation Navigation => Scope.ServiceProvider.GetRequiredService<WorkspaceNavigation>();
    private RunicNavigator Navigator => Scope.ServiceProvider.GetRequiredService<RunicNavigator>();
    private EditorViewModel Editor => Scope.ServiceProvider.GetRequiredService<EditorViewModel>();

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
        var sidebar = Host.Root.View<SidebarViewModel>(vm => vm.Sidebar);
        (await sidebar.ExecuteAsync(vm => vm.OpenNotesCommand)).EnsureOk();
        return Host.Root.View<DocumentViewModel>(vm => vm.Main).View<EditorViewModel>(vm => vm.CurrentPane);
    }
}

// One notes window: the application's own registrations, with the test clock in place
// of the system clock, and an optional change to them.
internal sealed class TestWindow : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private bool _disposed;

    public TestWindow(FakeTimeProvider clock, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddNotes()
            .AddRunicViews();
        configure?.Invoke(services);
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Scope = _services.CreateAsyncScope();
        var window = Scope.ServiceProvider;
        Host = new RunicWindowTestHost<ShellViewModel>(
            window.GetRequiredService<ShellViewModel>(),
            window.GetRequiredService<Func<IBridgeTransport, WindowContentSession, ShellViewModel, IDisposable>>(),
            new RunicWindowTestHostOptions
            {
                ViewLocator = window.GetRequiredService<IRunicViewLocator>(),
                // The window graph shares the navigator's model context.
                ModelContext = window.GetRequiredService<IRunicModelContext>(),
                TimeProvider = clock,
            });
    }

    public AsyncServiceScope Scope { get; }
    public RunicWindowTestHost<ShellViewModel> Host { get; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; // a test may close its window before the class disposes it
        _disposed = true;
        Host.Dispose();
        // The window scope disposes its navigator, which retires the entries it owns.
        await Scope.DisposeAsync();
        await _services.DisposeAsync();
    }
}
