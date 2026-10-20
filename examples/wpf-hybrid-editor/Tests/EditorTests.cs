using System.ComponentModel;
using HybridNotes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;

namespace HybridNotes.Tests;

public sealed class EditorTests
{
    [Test]
    public async Task Native_and_web_edits_share_validation_dirty_and_save_state()
    {
        await using var app = await TestApp.OpenAsync();
        using var web = app.Connect();
        await app.Context.InvokeAsync(() => app.Editor.Title = " ");
        await Assert.That(web.Root.Snapshot().Read(vm => vm.ValidationMessage)).IsEqualTo("Enter a title.");
        await Assert.That(web.Root.Snapshot().CanExecute(vm => vm.SaveCommand)).IsFalse();

        web.Root.Set(vm => vm.Title, "Updated").ThrowIfFailed();
        await Assert.That(app.Editor.Title).IsEqualTo("Updated");
        await Assert.That(app.Editor.IsDirty).IsTrue();
        var save = web.Root.Start(vm => vm.SaveCommand);
        app.Clock.Advance(TimeSpan.FromMilliseconds(350));
        await Assert.That((await save.WaitAsync()).Kind).IsEqualTo(RunicOperationStatusKind.Succeeded);
        await Assert.That(app.Store.Get(1).Title).IsEqualTo("Updated");
        await Assert.That(app.Editor.IsDirty).IsFalse();
        await Assert.That(web.Root.Snapshot().Read(vm => vm.Status)).IsEqualTo("Saved.");
    }

    [Test]
    public async Task Edits_made_while_saving_remain_dirty()
    {
        await using var app = await TestApp.OpenAsync();
        using var web = app.Connect();
        web.Root.Set(vm => vm.Body, "Submitted").ThrowIfFailed();
        var save = web.Root.Start(vm => vm.SaveCommand);
        web.Root.Set(vm => vm.Body, "Later edit").ThrowIfFailed();
        app.Clock.Advance(TimeSpan.FromMilliseconds(350));
        await save.WaitAsync();
        await Assert.That(app.Store.Get(1).Body).IsEqualTo("Submitted");
        await Assert.That(app.Editor.Body).IsEqualTo("Later edit");
        await Assert.That(web.Root.Snapshot().Read(vm => vm.IsDirty)).IsTrue();
    }

    [Test]
    public async Task Native_cancel_command_cancels_a_web_save_without_losing_the_draft()
    {
        await using var app = await TestApp.OpenAsync();
        using var web = app.Connect();
        web.Root.Set(vm => vm.Title, "Draft").ThrowIfFailed();
        var save = web.Root.Start(vm => vm.SaveCommand);
        await app.Context.InvokeAsync(() => app.Editor.SaveCancelCommand.Execute(null));
        await save.WaitAsync();
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(app.Store.Get(1).Title).IsEqualTo("Groceries");
        await Assert.That(app.Editor.Title).IsEqualTo("Draft");
        await Assert.That(app.Editor.IsDirty).IsTrue();
        await Assert.That(app.Editor.Status).IsEqualTo("Save cancelled.");
    }

    [Test]
    public async Task A_cancel_after_the_store_writes_keeps_the_save()
    {
        var storage = new LateCancelStore();
        await using var app = await TestApp.OpenAsync(storage);
        storage.AfterWrite = () => app.Editor.SaveCancelCommand.Execute(null);
        using var web = app.Connect();
        web.Root.Set(vm => vm.Title, "Written").ThrowIfFailed();
        (await web.Root.ExecuteAsync(vm => vm.SaveCommand)).ThrowIfFailed();
        await Assert.That(storage.Get(1).Title).IsEqualTo("Written");
        await Assert.That(app.Editor.IsDirty).IsFalse();
        await Assert.That(app.Editor.Status).IsEqualTo("Saved.");
        await Assert.That(web.Root.Snapshot().CanExecute(vm => vm.SaveCommand)).IsFalse();
    }

    [Test]
    public async Task A_storage_failure_is_shared_and_can_be_retried()
    {
        var storage = new FailingStore();
        await using var app = await TestApp.OpenAsync(storage);
        using var web = app.Connect();
        web.Root.Set(vm => vm.Title, "Retry").ThrowIfFailed();
        (await web.Root.ExecuteAsync(vm => vm.SaveCommand)).ThrowIfFailed();
        await Assert.That(web.Root.Snapshot().Read(vm => vm.Error)).IsEqualTo("Disk unavailable.");
        await Assert.That(app.Editor.IsDirty).IsTrue();
        storage.Fail = false;
        (await web.Root.ExecuteAsync(vm => vm.SaveCommand)).ThrowIfFailed();
        await Assert.That(app.Editor.Error).IsEqualTo("");
        await Assert.That(app.Editor.IsDirty).IsFalse();
        await Assert.That(storage.Get(1).Title).IsEqualTo("Retry");
    }

    [Test]
    public async Task Switching_and_reloading_presentation_preserve_model_and_navigation_identity()
    {
        await using var app = await TestApp.OpenAsync();
        var entry = app.Regions.Main.CurrentEntry;
        using (var firstWeb = app.Connect()) firstWeb.Root.Set(vm => vm.Body, "Draft survives").ThrowIfFailed();
        // Disposing the session represents web -> native. Native edits use the same model.
        await app.Context.InvokeAsync(() => app.Editor.Title = "Native edit");
        using (var secondWeb = app.Connect())
        {
            await Assert.That(secondWeb.Root.Snapshot().Read(vm => vm.Title)).IsEqualTo("Native edit");
            await Assert.That(secondWeb.Root.Snapshot().Read(vm => vm.Body)).IsEqualTo("Draft survives");
        }
        using var reloadedWeb = app.Connect();
        await Assert.That(reloadedWeb.Root.Snapshot().Read(vm => vm.IsDirty)).IsTrue();
        await Assert.That(app.Regions.Main.CurrentEntry).IsSameReferenceAs(entry);
        await Assert.That(app.Regions.Main.Current).IsSameReferenceAs(app.Editor);
        await Assert.That(app.Regions.Dialog.Current).IsNull(); // Presentation swaps never ask the navigation guard.
        await Assert.That(reloadedWeb.Root.Snapshot().CanExecute(vm => vm.SaveCommand)).IsTrue();
    }

    [Test]
    public async Task A_native_save_survives_web_session_recreation()
    {
        await using var app = await TestApp.OpenAsync();
        Task saving = Task.CompletedTask;
        await app.Context.InvokeAsync(() =>
        {
            app.Editor.Body = "Native save";
            saving = app.Editor.SaveCommand.ExecuteAsync(null);
        });
        using (var first = app.Connect()) await Assert.That(first.Root.Snapshot().IsExecuting(vm => vm.SaveCommand)).IsTrue();
        using var remounted = app.Connect();
        await Assert.That(remounted.Root.Snapshot().IsExecuting(vm => vm.SaveCommand)).IsTrue();
        app.Clock.Advance(TimeSpan.FromMilliseconds(350));
        await saving;
        await Assert.That(app.Store.Get(1).Body).IsEqualTo("Native save");
        await Assert.That(remounted.Root.Snapshot().Read(vm => vm.IsDirty)).IsFalse();
    }

    [Test]
    public async Task Closing_a_web_session_cancels_its_save_but_keeps_the_model_usable()
    {
        await using var app = await TestApp.OpenAsync();
        using (var first = app.Connect())
        {
            first.Root.Set(vm => vm.Title, "Uncommitted").ThrowIfFailed();
            first.Root.Start(vm => vm.SaveCommand);
        }
        await app.Editor.SaveCommand.ExecutionTask!;
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        using var remounted = app.Connect();
        await Assert.That(app.Store.Get(1).Title).IsEqualTo("Groceries");
        await Assert.That(remounted.Root.Snapshot().Read(vm => vm.Title)).IsEqualTo("Uncommitted");
        await Assert.That(remounted.Root.Snapshot().CanExecute(vm => vm.SaveCommand)).IsTrue();
    }

    [Test]
    public async Task A_rejected_departure_keeps_the_draft_and_confirmed_departure_discards_on_commit()
    {
        await using var app = await TestApp.OpenAsync();
        using var web = app.Connect();
        web.Root.Set(vm => vm.Title, "Draft").ThrowIfFailed();
        var entry = app.Regions.Main.CurrentEntry;
        var back = app.Regions.Main.BackAsync().AsTask();
        await WhenAsync(app.Regions.Dialog, () => app.Regions.Dialog.Current is ConfirmViewModel);
        await ((ConfirmViewModel)app.Regions.Dialog.Current!).NoCommand.ExecuteAsync(null);
        await Assert.That(await back).IsTypeOf<NavigationResult<object>.Rejected>();
        await Assert.That(app.Regions.Main.CurrentEntry).IsSameReferenceAs(entry);
        await Assert.That(app.Editor.IsDirty).IsTrue();

        bool? dirtyAtCommit = null;
        app.Regions.Main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NavigationRegion<object>.Current) && app.Regions.Main.Current is NotesListViewModel)
                dirtyAtCommit = app.Editor.IsDirty;
        };
        back = app.Regions.Main.BackAsync().AsTask();
        await WhenAsync(app.Regions.Dialog, () => app.Regions.Dialog.Current is ConfirmViewModel);
        await ((ConfirmViewModel)app.Regions.Dialog.Current!).YesCommand.ExecuteAsync(null);
        await Assert.That(await back).IsTypeOf<NavigationResult<object>.Committed>();
        await Assert.That(dirtyAtCommit).IsFalse();
        await Assert.That(app.Editor.Title).IsEqualTo("Groceries");
    }

    [Test]
    public async Task Cancelling_a_departure_dismisses_its_native_prompt_and_keeps_the_draft()
    {
        await using var app = await TestApp.OpenAsync();
        using var web = app.Connect();
        web.Root.Set(vm => vm.Body, "Keep this").ThrowIfFailed();
        using var cancellation = new CancellationTokenSource();
        var leaving = app.Regions.Main.BackAsync(cancellationToken: cancellation.Token).AsTask();
        await WhenAsync(app.Regions.Dialog, () => app.Regions.Dialog.Current is not null);
        cancellation.Cancel();
        var rejected = await Assert.That(await leaving).IsTypeOf<NavigationResult<object>.Rejected>();
        await Assert.That(rejected!.Reason).IsEqualTo(NavigationRejection.Cancelled);
        // A cancelled Back caller completes immediately; its prompt closes in a
        // separate transition. Drain that cleanup before asserting the UI state.
        await app.Regions.Main.Navigator.WhenIdleAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(app.Regions.Dialog.Current).IsNull();
        await Assert.That(app.Regions.Main.Current).IsSameReferenceAs(app.Editor);
        await Assert.That(app.Editor.Body).IsEqualTo("Keep this");
        await Assert.That(app.Editor.IsDirty).IsTrue();
    }

    [Test]
    public async Task A_running_save_blocks_departure_until_cancelled()
    {
        await using var app = await TestApp.OpenAsync();
        using var web = app.Connect();
        web.Root.Set(vm => vm.Title, "Draft").ThrowIfFailed();
        var save = web.Root.Start(vm => vm.SaveCommand);
        await Assert.That(await app.Regions.Main.BackAsync()).IsTypeOf<NavigationResult<object>.Rejected>();
        await Assert.That(app.Regions.Dialog.Current).IsNull();
        (await web.Root.ExecuteAsync(vm => vm.SaveCancelCommand)).ThrowIfFailed();
        await save.WaitAsync();
        await Assert.That(app.Editor.IsDirty).IsTrue();
    }

    private static async Task WhenAsync(INotifyPropertyChanged source, Func<bool> ready)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs e) { if (ready()) completion.TrySetResult(); }
        source.PropertyChanged += Changed;
        try { if (ready()) return; await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { source.PropertyChanged -= Changed; }
    }

    private sealed class LateCancelStore : INoteStore
    {
        private Note _note = new(1, "Groceries", "Milk");
        public Action? AfterWrite { get; set; }
        public IReadOnlyList<Note> All => [_note];
        public Note Get(int id) => _note;
        public Task SaveAsync(Note note, CancellationToken token)
        {
            _note = note;
            AfterWrite?.Invoke(); // Cancel arrives after the write has completed.
            return Task.CompletedTask;
        }
    }

    private sealed class FailingStore : INoteStore
    {
        private Note _note = new(1, "Groceries", "Milk");
        public bool Fail { get; set; } = true;
        public IReadOnlyList<Note> All => [_note];
        public Note Get(int id) => _note;
        public Task SaveAsync(Note note, CancellationToken token)
        {
            if (Fail) throw new IOException("Disk unavailable.");
            _note = note;
            return Task.CompletedTask;
        }
    }
}

internal sealed class TestApp : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;
    private TestApp(INoteStore? storage)
    {
        Clock = new FakeTimeProvider();
        var services = new ServiceCollection().AddSingleton<TimeProvider>(Clock).AddRunicNavigation().AddHybridNotes().AddRunicViews();
        if (storage is not null) services.AddSingleton(storage);
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _scope = _provider.CreateAsyncScope();
        Regions = _scope.ServiceProvider.GetRequiredService<AppRegions>();
        Context = _scope.ServiceProvider.GetRequiredService<IRunicModelContext>();
        Store = _scope.ServiceProvider.GetRequiredService<INoteStore>();
    }

    public FakeTimeProvider Clock { get; }
    public AppRegions Regions { get; }
    public IRunicModelContext Context { get; }
    public INoteStore Store { get; }
    public EditorViewModel Editor { get; private set; } = null!;

    public static async Task<TestApp> OpenAsync(INoteStore? storage = null)
    {
        var app = new TestApp(storage);
        await app.Regions.Main.ResetAsync<NotesListViewModel>();
        await app.Regions.Main.PushAsync<EditorViewModel, int>(1);
        app.Editor = (EditorViewModel)app.Regions.Main.Current!;
        return app;
    }

    public RunicWindowTestHost<EditorViewModel> Connect() => new(Editor,
        _scope.ServiceProvider.GetRequiredService<Func<IBridgeTransport, WindowContentSession, EditorViewModel, IDisposable>>(),
        new RunicWindowTestHostOptions { ModelContext = Context, TimeProvider = Clock });

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
    }
}
