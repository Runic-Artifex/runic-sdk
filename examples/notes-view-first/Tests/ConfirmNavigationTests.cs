using NotesWindowViews;
using Runic.Application.Testing;
using Runic.Navigation;

namespace NotesViewFirst.Tests;

// Unit tests of one page that takes its NavigationEntryContext, without a window: RunicTestNavigationEntry pushes the
// page on a test navigator, as the application's dialog region does.
public sealed class ConfirmNavigationTests
{
    [Test]
    public async Task Confirm_completes_the_request_and_leaves_the_dialog()
    {
        await using var dialog = await RunicTestNavigationEntry.CreateAsync(
            entry => new ConfirmNavigationViewModel(entry, "Discard the unsaved edits?"));
        await Assert.That(dialog.Content.Message).IsEqualTo("Discard the unsaved edits?");

        await dialog.Content.ConfirmCommand.ExecuteAsync(null);

        await Assert.That(await dialog.Completion).IsEqualTo(new NavigationCompletion<object?>.Completed(true));
        await Assert.That(dialog.Context.State).IsEqualTo(NavigationEntryState.Retired);
        await Assert.That(dialog.Region.Current).IsNull();
    }

    [Test]
    public async Task Cancel_dismisses_the_request()
    {
        await using var dialog = await RunicTestNavigationEntry.CreateAsync(
            entry => new ConfirmNavigationViewModel(entry, "Discard the unsaved edits?"));

        await dialog.Content.CancelCommand.ExecuteAsync(null);

        await Assert.That(await dialog.Completion).IsTypeOf<NavigationCompletion<object?>.Dismissed>();
        await Assert.That(dialog.Region.Current).IsNull();
    }

    [Test]
    public async Task A_covered_dialog_does_not_answer()
    {
        await using var dialog = await RunicTestNavigationEntry.CreateAsync(
            entry => new ConfirmNavigationViewModel(entry, "Discard the unsaved edits?"));
        await dialog.RetainAsync();

        // The entry is not current, so its Back is rejected and the request stays open.
        await dialog.Content.ConfirmCommand.ExecuteAsync(null);

        await Assert.That(dialog.Context.State).IsEqualTo(NavigationEntryState.Retained);
        await Assert.That(dialog.Completion.IsCompleted).IsFalse();
        await Assert.That((await dialog.ActivateAsync()).IsCommitted).IsTrue();
        await dialog.Content.ConfirmCommand.ExecuteAsync(null);
        await Assert.That(await dialog.Completion).IsEqualTo(new NavigationCompletion<object?>.Completed(true));
    }
}
