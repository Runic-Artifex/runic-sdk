using Microsoft.Extensions.DependencyInjection;
using MultiWindow;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Platform;

namespace MultiWindow.Tests;

// Opens the project Window with the window-level test host, so the application's own IRunicWindowService opens the
// document and account Windows as test Windows: a document opens once per path, and the account dialog is window-modal
// and returns the chosen account.
public sealed class DocumentAndAccountWindowTests : IAsyncDisposable
{
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"runic-multi-window-{Guid.NewGuid():N}", "settings.json");
    private readonly ServiceProvider _services;

    public DocumentAndAccountWindowTests() => _services = new ServiceCollection()
        .AddProject()
        .AddSingleton(new SettingsStore(_settingsPath))
        .AddSingleton<IDesktopSettings>(new ProjectWindowTests.FixedDesktopSettings(DesktopColorScheme.Light))
        .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    [Test]
    public async Task Opening_a_document_twice_focuses_its_window()
    {
        await using var project = await RunicWindowTestHost.CreateWindowAsync<ProjectWindow>(_services);
        var root = project.Root<ProjectViewModel>();
        (await root.ExecuteAsync(vm => vm.OpenDocumentCommand, "\"notes.md\"")).ThrowIfFailed();
        (await root.ExecuteAsync(vm => vm.OpenDocumentCommand, "\"plan.md\"")).ThrowIfFailed();
        (await root.ExecuteAsync(vm => vm.OpenDocumentCommand, "\"notes.md\"")).ThrowIfFailed();

        await Assert.That(project.OpenedWindows.Select(window => window.Key)).IsEquivalentTo(new object?[] { "notes.md", "plan.md" });
        var notes = project.OpenedWindows[0];
        await Assert.That(notes.Root<DocumentViewModel>().Snapshot().Read(vm => vm.Path)).IsEqualTo("notes.md");
        await Assert.That(notes.Presentation.PresentCount).IsEqualTo(1);

        // Closed from its page, the document opens in a new Window next time. The close ends the Window's own command, so
        // its reply is not checked.
        await notes.Root<DocumentViewModel>().ExecuteAsync(vm => vm.CloseCommand);
        (await root.ExecuteAsync(vm => vm.OpenDocumentCommand, "\"notes.md\"")).ThrowIfFailed();
        await Assert.That(project.OpenedWindows.Count).IsEqualTo(3);
        await Assert.That(notes.IsOpen).IsFalse();
    }

    [Test]
    public async Task The_account_dialog_blocks_the_project_and_returns_the_choice()
    {
        await using var project = await RunicWindowTestHost.CreateWindowAsync<ProjectWindow>(_services);
        var root = project.Root<ProjectViewModel>();
        var choosing = root.ExecuteAsync(vm => vm.ChooseAccountCommand);
        var dialog = await project.WaitForOpenedWindowAsync<AccountWindow>();
        await Assert.That(dialog.Owner).IsEqualTo(project);
        await Assert.That(project.IsBlocked).IsTrue();
        await Assert.That(dialog.Root<AccountViewModel>().Snapshot().Read(vm => vm.Current)).IsEqualTo("Household");

        (await dialog.Root<AccountViewModel>().ExecuteAsync(vm => vm.ChooseCommand, "\"Savings\"")).ThrowIfFailed();
        (await choosing).ThrowIfFailed();
        await Assert.That(root.Snapshot().Read(vm => vm.Account)).IsEqualTo("Savings");
        await Assert.That(project.IsBlocked).IsFalse();

        // Cancel returns no result: the account stays.
        choosing = root.ExecuteAsync(vm => vm.ChooseAccountCommand);
        dialog = await project.WaitForOpenedWindowAsync<AccountWindow>();
        await dialog.Root<AccountViewModel>().ExecuteAsync(vm => vm.CancelCommand);
        (await choosing).ThrowIfFailed();
        await Assert.That(root.Snapshot().Read(vm => vm.Account)).IsEqualTo("Savings");
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        var directory = Path.GetDirectoryName(_settingsPath)!;
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
