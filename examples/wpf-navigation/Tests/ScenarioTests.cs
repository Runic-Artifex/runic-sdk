using System.Windows;
using System.Windows.Controls;
using Runic.Navigation.Wpf;
using static Runic.Navigation.Examples.Notes.Tests.Scene;

namespace Runic.Navigation.Examples.Notes.Tests;

// The four comparison scenarios, plus the failure modes the comparison found in other libraries
// (examples/wpf-navigation/COMPARISON.md). Each test starts the app's own container and window.
internal static class ScenarioTests
{
    public static IEnumerable<(string Name, Func<Task> Test)> All =>
    [
        ("S1: the guard asks; Cancel keeps the edits, OK discards them", MasterDetailGuardAsync),
        ("S1: saved edits leave without asking, and the list reloads", SavedEditsLeaveAsync),
        ("S2: Delete asks and acts on the typed result", DeleteConfirmAsync),
        ("Toolkit command cancellation closes Delete without deleting", DeleteCancellationAsync),
        ("S3: the settings tabs are a child region of the page", NestedTabsAsync),
        ("S4: a double click on Back pops once", DoubleClickBackAsync),
        ("S4: two Backs from code pop once and never empty the region", DoubleBackFromCodeAsync),
        ("S4: Back while the confirm is open joins it; answered OK", () => BackWhileConfirmOpenAsync(confirm: true)),
        ("S4: Back while the confirm is open joins it; answered Cancel", () => BackWhileConfirmOpenAsync(confirm: false)),
        ("A page reached by Back still asks before losing edits", GuardOnPageReachedByBackAsync),
        ("A page keeps its argument and edits when it is shown again", ArgumentsSurviveReturnAsync),
        ("A superseded Back doesn't discard the edits", SupersededBackKeepsEditsAsync),
    ];

    private static async Task MasterDetailGuardAsync()
    {
        await using var scene = await StartAsync();
        var detail = await scene.OpenNoteAsync(1);
        Require(detail.Title == "Groceries" && scene.CanClickBack, $"The note opened as {scene.Path}.");
        scene.Type("Groceries and milk");

        scene.ClickBack();
        var confirm = await scene.ConfirmAsync();
        Require(confirm.Message == "Discard unsaved changes?", $"The guard asked '{confirm.Message}'.");
        Require(!scene.CanClickBack, "Back can be clicked while its confirm is open.");
        await confirm.NoCommand.ExecuteAsync(null);
        await scene.NoDialogAsync();
        await Settle();
        Require(scene.Main.Current == detail && detail.Title == "Groceries and milk" && detail.IsDirty,
            $"Cancel didn't keep the page and its edits: {scene.Path}.");

        scene.ClickBack();
        await (await scene.ConfirmAsync()).YesCommand.ExecuteAsync(null);
        await scene.PresentedAsync<NotesListViewModel>();
        // The discard ran when the Back committed; the store never saw the edit.
        Require(detail.Title == "Groceries" && scene.Store.Get(1).Title == "Groceries" && !scene.Main.CanGoBack,
            $"OK didn't discard and leave: {scene.Path}, note '{detail.Title}'.");
        await scene.NoDialogAsync();
    }

    private static async Task SavedEditsLeaveAsync()
    {
        await using var scene = await StartAsync();
        var list = scene.List;
        var detail = await scene.OpenNoteAsync(2);
        scene.Type("Release checklist v2");
        detail.SaveCommand.Execute(null);
        scene.ClickBack();
        await scene.PresentedAsync<NotesListViewModel>();
        Require(scene.Main.Current == list && scene.OpenDialogWindows == 0 && scene.Regions.Dialog.Current is null,
            "Leaving a saved note asked.");
        Require(list.Notes.Any(note => note.Title == "Release checklist v2"), "The list didn't reload when it was shown again.");
    }

    private static async Task DeleteConfirmAsync()
    {
        await using var scene = await StartAsync();
        var list = scene.List;
        var ideas = list.Notes.Single(note => note.Id == 3);

        var deleting = list.DeleteCommand.ExecuteAsync(ideas);
        var confirm = await scene.ConfirmAsync();
        Require(confirm.Message == "Delete 'Ideas'?", $"Delete asked '{confirm.Message}'.");
        await confirm.NoCommand.ExecuteAsync(null);
        await deleting;
        Require(list.Notes.Count == 3 && scene.Store.All.Count == 3, "Cancel deleted the note.");

        // Closing the dialog window is a dismissal too.
        deleting = list.DeleteCommand.ExecuteAsync(ideas);
        confirm = await scene.ConfirmAsync();
        Application.Current.Windows.OfType<Window>().Single(window => window.DataContext == confirm).Close();
        await deleting;
        Require(list.Notes.Count == 3, "Closing the confirm window deleted the note.");

        deleting = list.DeleteCommand.ExecuteAsync(ideas);
        await (await scene.ConfirmAsync()).YesCommand.ExecuteAsync(null);
        await deleting;
        Require(list.Notes.Count == 2 && scene.Store.All.All(note => note.Id != 3), "OK didn't delete the note.");
        await scene.NoDialogAsync();
    }

    private static async Task DeleteCancellationAsync()
    {
        await using var scene = await StartAsync();
        var list = scene.List;
        var ideas = list.Notes.Single(note => note.Id == 3);
        var deleting = list.DeleteCommand.ExecuteAsync(ideas);
        var confirm = await scene.ConfirmAsync();
        Require(list.DeleteCommand.CanBeCanceled, "The Toolkit command did not accept cancellation.");
        list.DeleteCommand.Cancel();
        await deleting.WaitAsync(Scene.Timeout);
        await scene.NoDialogAsync();
        Require(list.Notes.Count == 3 && scene.Store.All.Count == 3 && !list.DeleteCommand.IsRunning,
            "Cancelling Delete left its confirmation open or deleted the note.");
        // A late answer from the retired prompt cannot revive a cancelled request.
        await confirm.YesCommand.ExecuteAsync(null);
        Require(scene.Store.All.Count == 3, "A late answer deleted the note after command cancellation.");
    }

    private static async Task NestedTabsAsync()
    {
        await using var scene = await StartAsync();
        await scene.List.OpenSettingsCommand.ExecuteAsync(null);
        var settings = await scene.PresentedAsync<SettingsViewModel>();
        var general = (GeneralTabViewModel)settings.Tabs[0];
        var advanced = (AdvancedTabViewModel)settings.Tabs[1];
        var tabs = (TabControl)((UserControl)scene.View!).Content;
        await Until(() => TabShows<GeneralTabView>(tabs, general), "the General tab");

        general.ShowAdvancedCommand.Execute(null); // one tab selects another
        await Until(() => TabShows<AdvancedTabView>(tabs, advanced), "the Advanced tab");

        tabs.SelectedItem = general; // the user clicks a tab header
        await Until(() => TabShows<GeneralTabView>(tabs, general), "General again");
        Require(!settings.Tab.CanGoBack, "Switching tabs built up history.");

        scene.ClickBack();
        await scene.PresentedAsync<NotesListViewModel>();
        Require(!scene.CanClickBack, "Back is enabled at the root.");
        Require(await settings.Tab.ReplaceAsync(NavigationTarget.Borrow<object>(advanced))
            is NavigationResult<object>.Rejected { Reason: NavigationRejection.Closed }, "The tab region outlived its page.");
    }

    // The TabControl selects the tab, the child region has it current, and the tab's NavigationHost presents it.
    private static bool TabShows<TView>(TabControl tabs, object tab) where TView : FrameworkElement =>
        tabs.SelectedItem == tab && ((SettingsViewModel)tabs.DataContext).Tab.Current == tab
        && Descendant<NavigationHost>(tabs) is { Content: ContentPresenter { Content: TView { IsLoaded: true } view } } && view.DataContext == tab;

    private static async Task DoubleClickBackAsync()
    {
        await using var scene = await StartAsync();
        var detail = await scene.OpenNoteAsync(1);
        await scene.Main.PushAsync<SettingsViewModel>();
        await scene.PresentedAsync<SettingsViewModel>();

        scene.ClickBack();
        // No dispatcher work has run since the click: the Back is admitted, so Back can't execute.
        Require(scene.Main.IsTransitioning && !scene.CanClickBack, "Back can still be clicked right after a Back.");
        scene.ClickBack();
        System.Windows.Input.NavigationCommands.BrowseBack.Execute(null, scene.Host); // even without the CanExecute check
        await scene.PresentedAsync<NoteDetailViewModel>();
        await Settle();
        Require(scene.Main.Current == detail && scene.Main.History.Count == 1, $"A double click popped to {scene.Path}.");
    }

    private static async Task DoubleBackFromCodeAsync()
    {
        await using var scene = await StartAsync();
        await scene.OpenNoteAsync(1);
        var first = scene.Main.BackAsync().AsTask();
        var second = scene.Main.BackAsync().AsTask();
        var results = await Task.WhenAll(first, second);
        Require(results.All(result => result is NavigationResult<object>.Committed { Current.Content: NotesListViewModel })
            && results[0] is NavigationResult<object>.Committed a && results[1] is NavigationResult<object>.Committed b
            && a.Current!.Id == b.Current!.Id, $"Two Backs gave {results[0]} and {results[1]}.");
        await scene.PresentedAsync<NotesListViewModel>();
        Require(await scene.Main.BackAsync() is NavigationResult<object>.Rejected { Reason: NavigationRejection.NoHistory }
            && scene.Main.Current is NotesListViewModel, $"A Back without history left {scene.Path}.");
    }

    // A modal confirm blocks a second click, but a non-modal or overlay confirm, or code, can still go Back. The
    // second Back joins the first: one prompt, and answering it settles both without corrupting the history.
    private static async Task BackWhileConfirmOpenAsync(bool confirm)
    {
        await using var scene = await StartAsync();
        var list = scene.List;
        var first = await scene.OpenNoteAsync(1);
        await scene.Main.PushAsync<NoteDetailViewModel, int>(2);
        var second = await scene.PresentedAsync<NoteDetailViewModel>();
        scene.Type("Release checklist (edited)");

        scene.ClickBack();
        var prompt = await scene.ConfirmAsync();
        var again = scene.Main.BackAsync().AsTask();
        await Settle();
        Require(scene.Regions.Dialog.Current == prompt && !scene.Regions.Dialog.CanGoBack && scene.OpenDialogWindows == 1,
            "A second Back opened a second confirm.");

        if (confirm) await prompt.YesCommand.ExecuteAsync(null);
        else await prompt.NoCommand.ExecuteAsync(null);
        var result = await again;
        await scene.NoDialogAsync();
        await Settle();
        if (confirm)
            Require(result is NavigationResult<object>.Committed && scene.Main.Current == first && first.Title == "Groceries"
                && scene.Main.History.Single().Content == list, $"OK left {scene.Path}.");
        else
            Require(result is NavigationResult<object>.Rejected { Reason: NavigationRejection.Guard } && scene.Main.Current == second
                && second.IsDirty && scene.Main.History.Select(entry => entry.Content).SequenceEqual([list, first]),
                $"Cancel left {scene.Path}.");
    }

    // Detail, another page, Back, edit, Back: the guard of the page reached by Back is asked.
    private static async Task GuardOnPageReachedByBackAsync()
    {
        await using var scene = await StartAsync();
        var detail = await scene.OpenNoteAsync(1);
        await scene.Main.PushAsync<SettingsViewModel>();
        await scene.PresentedAsync<SettingsViewModel>();
        scene.ClickBack();
        Require(await scene.PresentedAsync<NoteDetailViewModel>() == detail, "Back didn't return to the note.");
        scene.Type("Groceries!");

        scene.ClickBack();
        await (await scene.ConfirmAsync()).YesCommand.ExecuteAsync(null);
        await scene.PresentedAsync<NotesListViewModel>();
        Require(scene.Store.Get(1).Title == "Groceries", "The edit reached the store.");
    }

    // The history keeps the entry, so the same ViewModel comes back with its argument and its draft.
    private static async Task ArgumentsSurviveReturnAsync()
    {
        await using var scene = await StartAsync();
        var detail = await scene.OpenNoteAsync(2);
        scene.Type("Release checklist (draft)");
        await scene.Main.PushAsync<SettingsViewModel>(); // covering a page doesn't ask: it keeps its state
        await scene.PresentedAsync<SettingsViewModel>();
        Require(scene.OpenDialogWindows == 0, "Covering the note asked.");

        scene.ClickBack();
        var shown = await scene.PresentedAsync<NoteDetailViewModel>();
        Require(shown == detail && shown.Title == "Release checklist (draft)" && Descendant<TextBox>(scene.View!)!.Text == shown.Title,
            "The note came back without its draft.");
        shown.SaveCommand.Execute(null);
        Require(scene.Store.Get(2).Title == "Release checklist (draft)", "The note lost its id.");
    }

    // The Back's confirm is open when code navigates elsewhere: the push supersedes the Back, the prompt
    // closes, and the edits stay, because the discard runs only when a departure commits.
    private static async Task SupersededBackKeepsEditsAsync()
    {
        await using var scene = await StartAsync();
        var detail = await scene.OpenNoteAsync(1);
        scene.Type("Groceries!");
        scene.ClickBack();
        await scene.ConfirmAsync();

        Require(await scene.Main.PushAsync<SettingsViewModel>() is NavigationResult<object>.Committed, "The push didn't commit.");
        await scene.NoDialogAsync();
        Require(detail.Title == "Groceries!" && detail.IsDirty, "A superseded Back discarded the edits.");

        scene.ClickBack();
        Require(await scene.PresentedAsync<NoteDetailViewModel>() == detail && detail.Title == "Groceries!", "The edits were lost.");
    }
}
