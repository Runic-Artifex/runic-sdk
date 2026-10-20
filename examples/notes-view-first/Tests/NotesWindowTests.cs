using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.DependencyInjection;
using NotesWindowViews;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;
using TUnit.Assertions.Enums;

namespace NotesViewFirst.Tests;

// Drives the real ViewModels and generated Bridges of one notes window, as the
// browser would, without a browser or native window.
public sealed class NotesWindowTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _clock = new();
    private readonly TestWindow _window;

    public NotesWindowTests() => _window = new TestWindow(_clock);

    private AsyncServiceScope Scope => _window.Scope;
    private RunicWindowTestHost<ShellViewModel> Host => _window.Host;

    [Test]
    public async Task The_window_opens_on_home_with_stable_content_routes()
    {
        var shell = Host.Root.Snapshot();

        // The test host numbers content in presentation order.
        await Assert.That(shell.Reference(vm => vm.Sidebar)).IsEqualTo(new PageReference("sidebar", "1"));
        await Assert.That(shell.Reference(vm => vm.Main)).IsEqualTo(new PageReference("home", "2"));
        await Assert.That(shell.Reference(vm => vm.Dialog)).IsNull();
        var home = Host.Root.View<HomeViewModel>(vm => vm.Main).Snapshot();
        await Assert.That(home.Read(vm => vm.Greeting)).IsEqualTo("Welcome to composed Notes");
        await Assert.That(home.Keys(vm => vm.RecentNotes)).IsEmpty();
    }

    [Test]
    public async Task Saving_waits_for_storage_and_lists_the_note_on_home()
    {
        var editor = await OpenEditorAsync();
        editor.Set(vm => vm.Title, "Groceries").ThrowIfFailed();
        editor.Set(vm => vm.Body, "Milk, eggs").ThrowIfFailed();

        var save = editor.Start(vm => vm.SaveCommand);
        await Assert.That(save.Admission).IsEqualTo(RunicOperationAdmissionKind.Accepted);
        await Assert.That(editor.Snapshot().IsExecuting(vm => vm.SaveCommand)).IsTrue();
        await Assert.That(save.GetStatus().Kind).IsEqualTo(RunicOperationStatusKind.Running);

        // Storage takes 250 ms of the test clock.
        _clock.Advance(TimeSpan.FromMilliseconds(250));
        await Assert.That((await save.WaitAsync()).Kind).IsEqualTo(RunicOperationStatusKind.Succeeded);
        var saved = editor.Snapshot();
        await Assert.That(saved.Read(vm => vm.SavedMessage)).IsEqualTo("Saved Groceries");
        await Assert.That(saved.Read(vm => vm.IsDirty)).IsFalse();

        var sidebar = Host.Root.View<SidebarViewModel>(vm => vm.Sidebar);
        (await sidebar.ExecuteAsync(vm => vm.OpenHomeCommand)).ThrowIfFailed();
        var home = Host.Root.View<HomeViewModel>(vm => vm.Main).Snapshot();
        await Assert.That(home.Keys(vm => vm.RecentNotes)).IsEquivalentTo(["Groceries"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_note_without_a_title_is_not_saved()
    {
        var editor = await OpenEditorAsync();
        editor.Set(vm => vm.Title, " ").ThrowIfFailed();

        var reply = await editor.ExecuteAsync(vm => vm.SaveCommand);

        await Assert.That(reply.Succeeded).IsFalse();
        // Save declares SaveFailure, so the client receives the typed failure.
        await Assert.That(reply.ErrorKind).IsEqualTo("domain-failed");
        await Assert.That(reply.Failure?.GetRawText()).IsEqualTo("""{"$case":"titleRequired"}""");
        await Assert.That(Scope.ServiceProvider.GetRequiredService<NotesLibrary>().Notes).IsEmpty();
    }

    [Test]
    public async Task A_note_cannot_take_the_title_of_another_note()
    {
        var editor = await OpenEditorAsync();
        Scope.ServiceProvider.GetRequiredService<NotesLibrary>().Record("Groceries", "Milk");
        editor.Set(vm => vm.Title, "Groceries").ThrowIfFailed();

        var reply = await editor.ExecuteAsync(vm => vm.SaveCommand);
        await Assert.That(reply.ErrorKind).IsEqualTo("domain-failed");
        await Assert.That(reply.Failure?.GetRawText()).IsEqualTo("""{"$case":"titleTaken","existingTitle":"Groceries"}""");

        // The operation path reports the same failure.
        var status = await editor.Start(vm => vm.SaveCommand).WaitAsync();
        await Assert.That(status.Kind).IsEqualTo(RunicOperationStatusKind.DomainFailed);
        await Assert.That(status.Failure?.GetProperty("$case").GetString()).IsEqualTo("titleTaken");
    }

    [Test]
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

        await Assert.That(state.Keys(vm => vm.RecentNotes)).IsEquivalentTo(["Groceries", "Ideas"], CollectionOrdering.Matching);
        await Assert.That(tracker.FullStates).IsEqualTo(0);
        await Assert.That(tracker.Changes).Contains(change => change.Kind == RunicCollectionChangeKind.Replace && change.Keys.SequenceEqual(["Groceries"]));
        await Assert.That(tracker.Changes).Contains(change => change.Kind == RunicCollectionChangeKind.Move && change.Index == 0);
        // The applied frames match the state .NET holds.
        tracker.Verify();
    }

    [Test]
    public async Task Leaving_unsaved_edits_asks_for_confirmation()
    {
        var editor = await OpenEditorAsync();
        editor.Set(vm => vm.Title, "Draft").ThrowIfFailed();
        var sidebar = Host.Root.View<SidebarViewModel>(vm => vm.Sidebar);

        // The Back waits in the document's guard until the dialog answers.
        var leaving = sidebar.Start(vm => vm.OpenHomeCommand);
        await WhenAsync(Navigation.Dialog, () => Navigation.Dialog.Current is not null);
        var dialog = Host.Root.View<ConfirmNavigationViewModel>(vm => vm.Dialog);
        await Assert.That(dialog.Snapshot().Read(vm => vm.Message)).IsEqualTo("Discard the unsaved edits and return Home?");

        (await dialog.ExecuteAsync(vm => vm.ConfirmCommand)).ThrowIfFailed();
        await Assert.That((await leaving.WaitAsync()).Kind).IsEqualTo(RunicOperationStatusKind.Succeeded);
        var shell = Host.Root.Snapshot();
        await Assert.That(shell.Reference(vm => vm.Dialog)).IsNull();
        await Assert.That(shell.Reference(vm => vm.Main)?.Kind).IsEqualTo("home");
        await Assert.That(Editor.IsDirty).IsFalse();
        await Assert.That(Editor.SavedMessage).IsEqualTo("Changes discarded.");
    }

    // #61: reject a Back through an asynchronous guard; current content, entry
    // identity and history are unchanged.
    [Test]
    public async Task Cancelling_the_dialog_keeps_the_document_entry_and_its_draft()
    {
        var editor = await OpenEditorAsync();
        editor.Set(vm => vm.Title, "Draft").ThrowIfFailed();
        var entry = Navigation.Main.CurrentEntry!;
        var history = Navigation.Main.History.ToArray();

        var leaving = Host.Root.View<SidebarViewModel>(vm => vm.Sidebar).Start(vm => vm.OpenHomeCommand);
        await WhenAsync(Navigation.Dialog, () => Navigation.Dialog.Current is not null);
        (await Host.Root.View<ConfirmNavigationViewModel>(vm => vm.Dialog).ExecuteAsync(vm => vm.CancelCommand)).ThrowIfFailed();
        await Assert.That((await leaving.WaitAsync()).Kind).IsEqualTo(RunicOperationStatusKind.Succeeded);

        await Assert.That(Navigation.Main.CurrentEntry).IsSameReferenceAs(entry);
        await Assert.That(entry.State).IsEqualTo(NavigationEntryState.Active);
        await Assert.That(Navigation.Main.History)
            .IsEquivalentTo(history, EqualityComparer<NavigationEntry<IMainViewModel>>.Default, CollectionOrdering.Matching);
        await Assert.That(Navigation.Dialog.Current).IsNull();
        await Assert.That(Host.Root.Snapshot().Reference(vm => vm.Dialog)).IsNull();
        await Assert.That(Host.Root.Snapshot().Reference(vm => vm.Main)?.Kind).IsEqualTo("document");
        await Assert.That(Host.Root.View<DocumentViewModel>(vm => vm.Main).View<EditorViewModel>(vm => vm.CurrentPane)
            .Snapshot().Read(vm => vm.Title)).IsEqualTo("Draft");
    }

    // #61: enter the editor, change the draft, go to the preview and back: the
    // same retained editor entry resumes with its draft.
    [Test]
    public async Task The_preview_round_trip_resumes_the_same_editor_entry()
    {
        var editor = await OpenEditorAsync();
        editor.Set(vm => vm.Body, "Milk").ThrowIfFailed();
        var document = (DocumentViewModel)Navigation.Main.Current!;
        var editorEntry = document.CurrentPane.CurrentEntry!;
        var documentView = Host.Root.View<DocumentViewModel>(vm => vm.Main);

        (await documentView.ExecuteAsync(vm => vm.ShowPreviewCommand)).ThrowIfFailed();
        await Assert.That(documentView.Snapshot().Read(vm => vm.ActivePane)).IsEqualTo(DocumentPane.Preview);
        await Assert.That(editorEntry.State).IsEqualTo(NavigationEntryState.Retained);
        await Assert.That(documentView.View<PreviewViewModel>(vm => vm.CurrentPane).Snapshot().Read(vm => vm.Excerpt)).IsEqualTo("Milk");

        (await documentView.ExecuteAsync(vm => vm.ShowEditorCommand)).ThrowIfFailed();
        await Assert.That(document.CurrentPane.CurrentEntry).IsSameReferenceAs(editorEntry);
        await Assert.That(editorEntry.State).IsEqualTo(NavigationEntryState.Active);
        await Assert.That(documentView.Snapshot().Read(vm => vm.ActivePane)).IsEqualTo(DocumentPane.Editor);
        await Assert.That(documentView.View<EditorViewModel>(vm => vm.CurrentPane).Snapshot().Read(vm => vm.Body)).IsEqualTo("Milk");
        // Resuming is not initializing: the document initialized once, and the editor entry is the same one.
        await Assert.That(document.Initializations).IsEqualTo(1);
        await Assert.That(document.CurrentPane.CurrentEntry!.Id).IsEqualTo(editorEntry.Id);
    }

    // #61: retiring the parent retires its owned child region once; the
    // borrowed editor and preview stay usable, and the next visit is a new entry.
    [Test]
    public async Task Leaving_the_document_retires_it_and_its_pane_but_not_the_borrowed_editor()
    {
        await OpenEditorAsync();
        var first = (DocumentViewModel)Navigation.Main.Current!;
        var firstEntry = Navigation.Main.CurrentEntry!;
        (await Host.Root.View<DocumentViewModel>(vm => vm.Main).ExecuteAsync(vm => vm.ShowPreviewCommand)).ThrowIfFailed();
        var paneEntry = first.CurrentPane.CurrentEntry!;

        (await Host.Root.View<SidebarViewModel>(vm => vm.Sidebar).ExecuteAsync(vm => vm.OpenHomeCommand)).ThrowIfFailed();
        await Assert.That(firstEntry.State).IsEqualTo(NavigationEntryState.Retired);
        await Assert.That(paneEntry.State).IsEqualTo(NavigationEntryState.Retired);
        await Assert.That(first.CurrentPane.Current).IsNull();
        await Assert.That(Navigator.UnretiredEntryCount).IsEqualTo(1);
        await Assert.That(Host.Root.View<SidebarViewModel>(vm => vm.Sidebar).Snapshot().Read(vm => vm.Selected)).IsEqualTo("Home");

        var editor = await OpenEditorAsync();
        await Assert.That(Navigation.Main.Current).IsNotSameReferenceAs(first);
        await Assert.That(Navigation.Main.CurrentEntry!.Id).IsNotEqualTo(firstEntry.Id);
        // The window-scoped editor is borrowed: the draft survives the visit.
        await Assert.That(((DocumentViewModel)Navigation.Main.Current!).CurrentPane.Current).IsSameReferenceAs(Editor);
        editor.Set(vm => vm.Title, "Still here").ThrowIfFailed();
        await Assert.That(Scope.ServiceProvider.GetRequiredService<PreviewViewModel>().Heading).IsEqualTo("Still here");
    }

    // #61: racing requests. Opening the notes twice creates one document. While the
    // dialog asks, the window's navigation is modal: a second Home or Notes request
    // does nothing, and the one Back that asks decides.
    [Test]
    public async Task Racing_navigation_has_one_winner()
    {
        var sidebar = Host.Root.View<SidebarViewModel>(vm => vm.Sidebar);
        var notes = Navigation.OpenNotesAsync();
        await Task.WhenAll(notes, Navigation.OpenNotesAsync());
        // Home, one document and its editor pane.
        await Assert.That(Navigator.UnretiredEntryCount).IsEqualTo(3);
        await Assert.That(Navigation.Main.Current?.GetType()).IsEqualTo(typeof(DocumentViewModel));
        await Assert.That(Navigation.Main.History).HasSingleItem();

        await Scope.ServiceProvider.GetRequiredService<IRunicModelContext>().InvokeAsync(() => Editor.Title = "Draft");
        var leaving = Navigation.OpenHomeAsync();
        await WhenAsync(Navigation.Dialog, () => Navigation.Dialog.Current is not null);
        var dialog = Navigation.Dialog.CurrentEntry!;
        await Navigation.OpenHomeAsync();
        await Navigation.OpenNotesAsync();
        await Assert.That(Navigation.Dialog.CurrentEntry).IsSameReferenceAs(dialog);
        await Assert.That(leaving.IsCompleted).IsFalse();

        (await Host.Root.View<ConfirmNavigationViewModel>(vm => vm.Dialog).ExecuteAsync(vm => vm.ConfirmCommand)).ThrowIfFailed();
        await leaving;
        await Assert.That(Navigation.Main.Current?.GetType()).IsEqualTo(typeof(HomeViewModel));
        await Assert.That(Navigation.Dialog.Current).IsNull();
        await Assert.That(sidebar.Snapshot().Read(vm => vm.Selected)).IsEqualTo("Home");
        await Assert.That(Navigator.UnretiredEntryCount).IsEqualTo(1);
    }

    // The confirm is modal: the sidebar and the document's pane commands report that they
    // are unavailable while it asks, and the Bridge rejects them, so a pane change can't
    // supersede the Back and dismiss the dialog.
    [Test]
    public async Task Navigation_commands_are_unavailable_while_the_dialog_asks()
    {
        var editor = await OpenEditorAsync();
        editor.Set(vm => vm.Title, "Draft").ThrowIfFailed();
        var sidebar = Host.Root.View<SidebarViewModel>(vm => vm.Sidebar);
        var documentView = Host.Root.View<DocumentViewModel>(vm => vm.Main);
        await Assert.That(sidebar.Snapshot().CanExecute(vm => vm.OpenHomeCommand)).IsTrue();
        await Assert.That(documentView.Snapshot().CanExecute(vm => vm.ShowPreviewCommand)).IsTrue();

        var leaving = sidebar.Start(vm => vm.OpenHomeCommand);
        await WhenAsync(Navigation.Dialog, () => Navigation.Dialog.Current is not null);
        var sidebarState = sidebar.Snapshot();
        await Assert.That(sidebarState.CanExecute(vm => vm.OpenHomeCommand)).IsFalse();
        await Assert.That(sidebarState.CanExecute(vm => vm.OpenNotesCommand)).IsFalse();
        var documentState = documentView.Snapshot();
        await Assert.That(documentState.CanExecute(vm => vm.ShowEditorCommand)).IsFalse();
        await Assert.That(documentState.CanExecute(vm => vm.ShowPreviewCommand)).IsFalse();
        await Assert.That((await documentView.ExecuteAsync(vm => vm.ShowPreviewCommand)).ErrorKind).IsEqualTo("rejected");
        await Assert.That((await sidebar.ExecuteAsync(vm => vm.OpenNotesCommand)).ErrorKind).IsEqualTo("rejected");
        await Assert.That(Navigation.Dialog.Current).IsNotNull();
        await Assert.That(documentView.Snapshot().Read(vm => vm.ActivePane)).IsEqualTo(DocumentPane.Editor);

        (await Host.Root.View<ConfirmNavigationViewModel>(vm => vm.Dialog).ExecuteAsync(vm => vm.CancelCommand)).ThrowIfFailed();
        await Assert.That((await leaving.WaitAsync()).Kind).IsEqualTo(RunicOperationStatusKind.Succeeded);
        await Assert.That(sidebar.Snapshot().CanExecute(vm => vm.OpenHomeCommand)).IsTrue();
        await Assert.That(documentView.Snapshot().CanExecute(vm => vm.ShowPreviewCommand)).IsTrue();
    }

    // Cancel always ends the question, also when its answer can't commit. Here another
    // dialog covers the confirm, so the confirm's Back is rejected as NotCurrent; Cancel
    // then dismisses the request, and the guard keeps the document.
    [Test]
    public async Task Cancel_ends_the_question_when_its_answer_cannot_commit()
    {
        var editor = await OpenEditorAsync();
        editor.Set(vm => vm.Title, "Draft").ThrowIfFailed();
        var leaving = Navigation.OpenHomeAsync();
        await WhenAsync(Navigation.Dialog, () => Navigation.Dialog.Current is not null);
        var confirm = (ConfirmNavigationViewModel)Navigation.Dialog.Current!;
        // The navigator constructs the cover, as it does the confirm, and passes it its entry.
        await Assert.That(await Navigation.Dialog.PushAsync(NavigationTarget.Create<IDialogViewModel>((_, entry) =>
                new ConfirmNavigationViewModel(entry, "Covering"))))
            .IsTypeOf<NavigationResult<IDialogViewModel>.Committed>();
        var cover = (ConfirmNavigationViewModel)Navigation.Dialog.Current!;
        await Assert.That(cover).IsNotSameReferenceAs(confirm);

        await confirm.CancelCommand.ExecuteAsync(null);
        await leaving.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(Navigation.Main.Current?.GetType()).IsEqualTo(typeof(DocumentViewModel));
        await Assert.That(Editor.IsDirty).IsTrue();
        await Assert.That(Editor.Title).IsEqualTo("Draft");

        // The dismissed confirm stays below the cover; when it returns, Cancel closes it.
        await Assert.That(Navigation.Dialog.Current).IsSameReferenceAs(cover);
        await Navigation.Dialog.BackAsync();
        await Assert.That(Navigation.Dialog.Current).IsSameReferenceAs(confirm);
        await confirm.CancelCommand.ExecuteAsync(null);
        await Assert.That(Navigation.Dialog.Current).IsNull();
        await Assert.That(Navigation.CanNavigate).IsTrue();
    }

    // A confirmed departure discards the draft in the turn that commits the Back, before the
    // regions raise their changes: a handler of Main's Current already sees the discarded draft.
    [Test]
    public async Task A_confirmed_departure_discards_the_draft_in_the_commit_turn()
    {
        var editor = await OpenEditorAsync();
        editor.Set(vm => vm.Title, "Draft").ThrowIfFailed();
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
            (await Host.Root.View<ConfirmNavigationViewModel>(vm => vm.Dialog).ExecuteAsync(vm => vm.ConfirmCommand)).ThrowIfFailed();
            await Assert.That((await leaving.WaitAsync()).Kind).IsEqualTo(RunicOperationStatusKind.Succeeded);
        }
        finally { Navigation.Main.PropertyChanged -= OnMainChanged; }
        await Assert.That(Navigation.Main.Current?.GetType()).IsEqualTo(typeof(HomeViewModel));
        await Assert.That(dirtyWhenHomeShows).IsFalse();
        await Assert.That(draft.IsDirty).IsFalse();
        await Assert.That(draft.Title).IsEqualTo("Untitled");
    }

    // A confirmed guard whose Back is then superseded keeps the document and its draft. The
    // yes ends with the Back, and the discard runs only in a commit turn of a departure the
    // guard allowed: closing the window afterwards, which clears Main without asking guards,
    // does not discard the draft.
    [Test]
    public async Task A_superseded_confirmed_back_keeps_the_draft_through_window_close()
    {
        var editor = await OpenEditorAsync();
        editor.Set(vm => vm.Title, "Draft").ThrowIfFailed();
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
        (await Host.Root.View<ConfirmNavigationViewModel>(vm => vm.Dialog).ExecuteAsync(vm => vm.ConfirmCommand)).ThrowIfFailed();
        await Assert.That((await leaving.WaitAsync()).Kind).IsEqualTo(RunicOperationStatusKind.Succeeded);
        await Assert.That(await superseding.Task.WaitAsync(TimeSpan.FromSeconds(10))).IsTypeOf<NavigationResult<IMainViewModel>.Rejected>();
        await Assert.That(Navigation.Main.Current).IsSameReferenceAs(document);
        await Assert.That(Editor.IsDirty).IsTrue();

        await _window.DisposeAsync();
        await Assert.That(draft.Title).IsEqualTo("Draft");
        await Assert.That(draft.IsDirty).IsTrue();
    }

    // #61: a document that fails to prepare leaves Home current and nothing tracked, and
    // the next visit works. Notes has no reset path, so a guard-rejected reset has no Notes test.
    [Test]
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

        (await sidebar.ExecuteAsync(vm => vm.OpenNotesCommand)).ThrowIfFailed();
        await Assert.That(navigation.Main.Current?.GetType()).IsEqualTo(typeof(HomeViewModel));
        await Assert.That(navigator.UnretiredEntryCount).IsEqualTo(1);
        await Assert.That(window.Host.Root.Snapshot().Reference(vm => vm.Main)?.Kind).IsEqualTo("home");

        (await sidebar.ExecuteAsync(vm => vm.OpenNotesCommand)).ThrowIfFailed();
        await Assert.That(navigation.Main.Current?.GetType()).IsEqualTo(typeof(DocumentViewModel));
        await Assert.That(navigator.UnretiredEntryCount).IsEqualTo(3);
        await Assert.That(attempts).IsEqualTo(2);
    }

    // Closing the window while the dialog asks dismisses it and retires every entry.
    [Test]
    public async Task Closing_the_window_while_the_dialog_asks_retires_everything()
    {
        await OpenEditorAsync();
        Editor.Title = "Draft";
        var leaving = Navigation.OpenHomeAsync();
        await WhenAsync(Navigation.Dialog, () => Navigation.Dialog.Current is not null);

        await Navigator.DisposeAsync();
        await leaving;
        await Assert.That(Navigator.UnretiredEntryCount).IsEqualTo(0);
        await Assert.That(Editor.IsDirty).IsTrue();
    }

    public async ValueTask DisposeAsync() => await _window.DisposeAsync();

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
        (await sidebar.ExecuteAsync(vm => vm.OpenNotesCommand)).ThrowIfFailed();
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
        // Resolves the shell, its generated Bridge, the View locator and the model
        // context the navigator shares from the window's scope.
        Host = RunicWindowTestHost.Create<ShellViewModel>(Scope.ServiceProvider,
            new RunicWindowTestHostOptions { TimeProvider = clock });
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
