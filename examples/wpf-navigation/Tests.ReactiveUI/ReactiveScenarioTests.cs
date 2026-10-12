using System.Windows;
using System.Windows.Controls;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Signals;
using static Runic.Navigation.Examples.Notes.Tests.Scene;

namespace Runic.Navigation.Examples.Notes.Tests;

// The ReactiveUI variant (../NotesNavigation.ReactiveUI) through the same scenarios: the leave guard asks through
// the ViewModel's Interaction, the confirm answers with the adapter's result commands, and the list reloads in
// WhenNavigatedTo. The View answers the interaction with a MessageBox; a test registers its own handler after the
// View has activated, and ReactiveUI asks the latest handler first.
internal sealed class ReactiveScenarioTests
{
    [Test, DisplayName("S1: the guard asks through the interaction; Cancel keeps the edits, OK discards them"), NotInParallel("WpfSuite", Order = 1)]
    public async Task MasterDetailGuardAsync()
    {
        await using var scene = await StartAsync();
        var detail = await scene.OpenNoteAsync(1);
        Require(scene.View is NoteDetailView { ViewModel: var bound } && bound == detail, "The View isn't bound to the note.");
        scene.Type("Groceries and milk");
        await Settle(); // the View's WhenActivated has registered its handler
        var answers = new Queue<bool>([false, true]);
        var prompts = new List<string>();
        var canClickBackWhileAsked = true;
        using var handler = detail.ConfirmDiscard.RegisterHandler(context =>
        {
            prompts.Add(context.Input);
            canClickBackWhileAsked = scene.CanClickBack;
            context.SetOutput(answers.Dequeue());
        });

        scene.ClickBack();
        await Settle();
        Require(prompts is ["Discard unsaved changes?"] && !canClickBackWhileAsked, $"The guard asked {string.Join(", ", prompts)}.");
        Require(scene.Main.Current == detail && detail.Title == "Groceries and milk" && detail.IsDirty,
            $"Cancel didn't keep the page and its edits: {scene.Path}.");

        scene.ClickBack();
        await scene.PresentedAsync<NotesListViewModel>();
        // The discard ran when the Back committed; the store never saw the edit.
        Require(prompts.Count == 2 && detail.Title == "Groceries" && scene.Store.Get(1).Title == "Groceries" && !scene.Main.CanGoBack,
            $"OK didn't discard and leave: {scene.Path}, note '{detail.Title}'.");
    }

    [Test, DisplayName("S1: saved edits leave without asking, and WhenNavigatedTo reloads the list"), NotInParallel("WpfSuite", Order = 2)]
    public async Task SavedEditsLeaveAsync()
    {
        await using var scene = await StartAsync();
        var list = scene.List;
        var detail = await scene.OpenNoteAsync(2);
        scene.Type("Release checklist v2");
        await detail.SaveCommand.Execute();
        await Settle();
        var asked = 0;
        using var handler = detail.ConfirmDiscard.RegisterHandler(context => { asked++; context.SetOutput(false); });
        scene.ClickBack();
        await scene.PresentedAsync<NotesListViewModel>();
        Require(scene.Main.Current == list && asked == 0, "Leaving a saved note asked.");
        Require(list.Notes.Any(note => note.Title == "Release checklist v2"), "The list didn't reload when it was shown again.");
    }

    [Test, DisplayName("S2: Delete asks with PushForResult and acts on the typed result"), NotInParallel("WpfSuite", Order = 3)]
    public async Task DeleteConfirmAsync()
    {
        await using var scene = await StartAsync();
        var list = scene.List;
        list.Selected = list.Notes.Single(note => note.Id == 3);

        var deleting = Run(list.DeleteCommand.Execute());
        var confirm = await scene.ConfirmAsync();
        Require(confirm.Message == "Delete 'Ideas'?", $"Delete asked '{confirm.Message}'.");
        Require((await confirm.NoCommand.Execute()).Kind == NavigationOutcomeKind.Committed, "No didn't close the confirm.");
        await deleting;
        Require(list.Notes.Count == 3 && scene.Store.All.Count == 3, "Cancel deleted the note.");

        // Closing the dialog window is a dismissal too.
        deleting = Run(list.DeleteCommand.Execute());
        confirm = await scene.ConfirmAsync();
        Application.Current.Windows.OfType<Window>().Single(window => window.DataContext == confirm).Close();
        await deleting;
        Require(list.Notes.Count == 3, "Closing the confirm window deleted the note.");

        deleting = Run(list.DeleteCommand.Execute());
        await (await scene.ConfirmAsync()).YesCommand.Execute();
        await deleting;
        Require(list.Notes.Count == 2 && scene.Store.All.All(note => note.Id != 3), "OK didn't delete the note.");
        await scene.NoDialogAsync();
    }

    [Test, DisplayName("S2: disposing the Delete execution closes the confirm without deleting"), NotInParallel("WpfSuite", Order = 4)]
    public async Task DeleteCancellationAsync()
    {
        await using var scene = await StartAsync();
        var list = scene.List;
        list.Selected = list.Notes.Single(note => note.Id == 3);
        var execution = list.DeleteCommand.Execute().Subscribe(_ => { });
        var confirm = await scene.ConfirmAsync();
        execution.Dispose(); // cancels the command's token, which PushForResult turns into a dismissal
        await scene.NoDialogAsync();
        Require(list.Notes.Count == 3 && scene.Store.All.Count == 3, "Cancelling Delete deleted the note.");
        // A late answer from the retired prompt cannot revive a cancelled request.
        var late = await confirm.YesCommand.Execute();
        Require(late.Kind == NavigationOutcomeKind.Rejected && scene.Store.All.Count == 3,
            $"A late answer was {late.Kind} after command cancellation.");
    }

    [Test, DisplayName("S3: the settings tabs are a child region of the page"), NotInParallel("WpfSuite", Order = 5)]
    public async Task NestedTabsAsync()
    {
        await using var scene = await StartAsync();
        await scene.List.OpenSettingsCommand.Execute();
        var settings = await scene.PresentedAsync<SettingsViewModel>();
        var general = (GeneralTabViewModel)settings.Tabs[0];
        var tabs = (TabControl)((UserControl)scene.View!).Content;
        await Until(() => tabs.SelectedItem == general && settings.Tab.Current == general, "the General tab");
        await general.ShowAdvancedCommand.Execute();
        await Until(() => tabs.SelectedItem == settings.Tabs[1] && settings.Tab.Current == settings.Tabs[1], "the Advanced tab");
        scene.ClickBack();
        await scene.PresentedAsync<NotesListViewModel>();
        Require(!scene.CanClickBack, "Back is enabled at the root.");
    }

    // The question is open when code navigates elsewhere: the push supersedes the Back, the guard's token disposes
    // the Handle subscription, and the edits stay, because the discard runs only when a departure commits.
    [Test, DisplayName("A superseded Back doesn't discard the edits"), NotInParallel("WpfSuite", Order = 6)]
    public async Task SupersededBackKeepsEditsAsync()
    {
        await using var scene = await StartAsync();
        var detail = await scene.OpenNoteAsync(1);
        scene.Type("Groceries!");
        await Settle();
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = detail.ConfirmDiscard.RegisterHandler(async context =>
        {
            asked.TrySetResult();
            context.SetOutput(await answer.Task);
        });
        scene.ClickBack();
        await asked.Task.WaitAsync(Scene.Timeout);

        Require(await scene.Main.PushAsync<SettingsViewModel>() is NavigationResult<object>.Committed, "The push didn't commit.");
        answer.SetResult(true); // the late yes of the superseded question is dropped
        await Settle();
        Require(detail.Title == "Groceries!" && detail.IsDirty, "A superseded Back discarded the edits.");

        scene.ClickBack();
        Require(await scene.PresentedAsync<NoteDetailViewModel>() == detail && detail.Title == "Groceries!", "The edits were lost.");
    }

    // Starts an execution now, as a binding would, and awaits its end.
    private static async Task Run<T>(IObservable<T> execution) => await execution;
}
