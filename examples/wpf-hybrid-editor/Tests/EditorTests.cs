using System.ComponentModel;
using HybridNotes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;
using Xunit;

namespace HybridNotes.Tests;

public sealed class EditorTests
{
    [Fact]
    public async Task Native_and_web_edits_share_validation_dirty_and_save_state()
    {
        await using var app = await TestApp.OpenAsync();
        using var web = app.Connect();
        await app.Context.InvokeAsync(() => app.Editor.Title = " ");
        Assert.Equal("Enter a title.", web.Root.Snapshot().Read(vm => vm.ValidationMessage));
        Assert.False(web.Root.Snapshot().CanExecute(vm => vm.SaveCommand));

        web.Root.Set(vm => vm.Title, "Updated").EnsureOk();
        Assert.Equal("Updated", app.Editor.Title);
        Assert.True(app.Editor.IsDirty);
        var save = web.Root.Start(vm => vm.SaveCommand);
        app.Clock.Advance(TimeSpan.FromMilliseconds(350));
        Assert.Equal("succeeded", (await save.WaitAsync()).Kind);
        Assert.Equal("Updated", app.Store.Get(1).Title);
        Assert.False(app.Editor.IsDirty);
        Assert.Equal("Saved.", web.Root.Snapshot().Read(vm => vm.Status));
    }

    [Fact]
    public async Task Edits_made_while_saving_remain_dirty()
    {
        await using var app = await TestApp.OpenAsync();
        using var web = app.Connect();
        web.Root.Set(vm => vm.Body, "Submitted").EnsureOk();
        var save = web.Root.Start(vm => vm.SaveCommand);
        web.Root.Set(vm => vm.Body, "Later edit").EnsureOk();
        app.Clock.Advance(TimeSpan.FromMilliseconds(350));
        await save.WaitAsync();
        Assert.Equal("Submitted", app.Store.Get(1).Body);
        Assert.Equal("Later edit", app.Editor.Body);
        Assert.True(web.Root.Snapshot().Read(vm => vm.IsDirty));
    }

    [Fact]
    public async Task Native_cancel_command_cancels_a_web_save_without_losing_the_draft()
    {
        await using var app = await TestApp.OpenAsync();
        using var web = app.Connect();
        web.Root.Set(vm => vm.Title, "Draft").EnsureOk();
        var save = web.Root.Start(vm => vm.SaveCommand);
        await app.Context.InvokeAsync(() => app.Editor.SaveCancelCommand.Execute(null));
        await save.WaitAsync();
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("Groceries", app.Store.Get(1).Title);
        Assert.Equal("Draft", app.Editor.Title);
        Assert.True(app.Editor.IsDirty);
        Assert.Equal("Save cancelled.", app.Editor.Status);
    }

    [Fact]
    public async Task A_storage_failure_is_shared_and_can_be_retried()
    {
        var storage = new FailingStore();
        await using var app = await TestApp.OpenAsync(storage);
        using var web = app.Connect();
        web.Root.Set(vm => vm.Title, "Retry").EnsureOk();
        (await web.Root.ExecuteAsync(vm => vm.SaveCommand)).EnsureOk();
        Assert.Equal("Disk unavailable.", web.Root.Snapshot().Read(vm => vm.Error));
        Assert.True(app.Editor.IsDirty);
        storage.Fail = false;
        (await web.Root.ExecuteAsync(vm => vm.SaveCommand)).EnsureOk();
        Assert.Equal("", app.Editor.Error);
        Assert.False(app.Editor.IsDirty);
        Assert.Equal("Retry", storage.Get(1).Title);
    }

    [Fact]
    public async Task Switching_and_reloading_presentation_preserve_model_and_navigation_identity()
    {
        await using var app = await TestApp.OpenAsync();
        var entry = app.Regions.Main.CurrentEntry;
        using (var firstWeb = app.Connect()) firstWeb.Root.Set(vm => vm.Body, "Draft survives").EnsureOk();
        // Disposing the session represents web -> native. Native edits use the same model.
        await app.Context.InvokeAsync(() => app.Editor.Title = "Native edit");
        using (var secondWeb = app.Connect())
        {
            Assert.Equal("Native edit", secondWeb.Root.Snapshot().Read(vm => vm.Title));
            Assert.Equal("Draft survives", secondWeb.Root.Snapshot().Read(vm => vm.Body));
        }
        using var reloadedWeb = app.Connect();
        Assert.True(reloadedWeb.Root.Snapshot().Read(vm => vm.IsDirty));
        Assert.Same(entry, app.Regions.Main.CurrentEntry);
        Assert.Same(app.Editor, app.Regions.Main.Current);
        Assert.Null(app.Regions.Dialog.Current); // Presentation swaps never ask the navigation guard.
        Assert.True(reloadedWeb.Root.Snapshot().CanExecute(vm => vm.SaveCommand));
    }

    [Fact]
    public async Task A_native_save_survives_web_session_recreation()
    {
        await using var app = await TestApp.OpenAsync();
        Task saving = Task.CompletedTask;
        await app.Context.InvokeAsync(() =>
        {
            app.Editor.Body = "Native save";
            saving = app.Editor.SaveCommand.ExecuteAsync(null);
        });
        using (var first = app.Connect()) Assert.True(first.Root.Snapshot().IsExecuting(vm => vm.SaveCommand));
        using var remounted = app.Connect();
        Assert.True(remounted.Root.Snapshot().IsExecuting(vm => vm.SaveCommand));
        app.Clock.Advance(TimeSpan.FromMilliseconds(350));
        await saving;
        Assert.Equal("Native save", app.Store.Get(1).Body);
        Assert.False(remounted.Root.Snapshot().Read(vm => vm.IsDirty));
    }

    [Fact]
    public async Task Closing_a_web_session_cancels_its_save_but_keeps_the_model_usable()
    {
        await using var app = await TestApp.OpenAsync();
        using (var first = app.Connect())
        {
            first.Root.Set(vm => vm.Title, "Uncommitted").EnsureOk();
            first.Root.Start(vm => vm.SaveCommand);
        }
        await app.Editor.SaveCommand.ExecutionTask!;
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        using var remounted = app.Connect();
        Assert.Equal("Groceries", app.Store.Get(1).Title);
        Assert.Equal("Uncommitted", remounted.Root.Snapshot().Read(vm => vm.Title));
        Assert.True(remounted.Root.Snapshot().CanExecute(vm => vm.SaveCommand));
    }

    [Fact]
    public async Task A_rejected_departure_keeps_the_draft_and_confirmed_departure_discards_on_commit()
    {
        await using var app = await TestApp.OpenAsync();
        using var web = app.Connect();
        web.Root.Set(vm => vm.Title, "Draft").EnsureOk();
        var entry = app.Regions.Main.CurrentEntry;
        var back = app.Regions.Main.BackAsync().AsTask();
        await WhenAsync(app.Regions.Dialog, () => app.Regions.Dialog.Current is ConfirmViewModel);
        await ((ConfirmViewModel)app.Regions.Dialog.Current!).NoCommand.ExecuteAsync(null);
        Assert.IsType<NavigationResult<object>.Rejected>(await back);
        Assert.Same(entry, app.Regions.Main.CurrentEntry);
        Assert.True(app.Editor.IsDirty);

        bool? dirtyAtCommit = null;
        app.Regions.Main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NavigationRegion<object>.Current) && app.Regions.Main.Current is NotesListViewModel)
                dirtyAtCommit = app.Editor.IsDirty;
        };
        back = app.Regions.Main.BackAsync().AsTask();
        await WhenAsync(app.Regions.Dialog, () => app.Regions.Dialog.Current is ConfirmViewModel);
        await ((ConfirmViewModel)app.Regions.Dialog.Current!).YesCommand.ExecuteAsync(null);
        Assert.IsType<NavigationResult<object>.Committed>(await back);
        Assert.False(dirtyAtCommit);
        Assert.Equal("Groceries", app.Editor.Title);
    }

    [Fact]
    public async Task Cancelling_a_departure_dismisses_its_native_prompt_and_keeps_the_draft()
    {
        await using var app = await TestApp.OpenAsync();
        using var web = app.Connect();
        web.Root.Set(vm => vm.Body, "Keep this").EnsureOk();
        using var cancellation = new CancellationTokenSource();
        var leaving = app.Regions.Main.BackAsync(cancellationToken: cancellation.Token).AsTask();
        await WhenAsync(app.Regions.Dialog, () => app.Regions.Dialog.Current is not null);
        cancellation.Cancel();
        await leaving;
        Assert.Null(app.Regions.Dialog.Current);
        Assert.Same(app.Editor, app.Regions.Main.Current);
        Assert.Equal("Keep this", app.Editor.Body);
        Assert.True(app.Editor.IsDirty);
    }

    [Fact]
    public async Task A_running_save_blocks_departure_until_cancelled()
    {
        await using var app = await TestApp.OpenAsync();
        using var web = app.Connect();
        web.Root.Set(vm => vm.Title, "Draft").EnsureOk();
        var save = web.Root.Start(vm => vm.SaveCommand);
        Assert.IsType<NavigationResult<object>.Rejected>(await app.Regions.Main.BackAsync());
        Assert.Null(app.Regions.Dialog.Current);
        (await web.Root.ExecuteAsync(vm => vm.SaveCancelCommand)).EnsureOk();
        await save.WaitAsync();
        Assert.True(app.Editor.IsDirty);
    }

    private static async Task WhenAsync(INotifyPropertyChanged source, Func<bool> ready)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs e) { if (ready()) completion.TrySetResult(); }
        source.PropertyChanged += Changed;
        try { if (ready()) return; await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { source.PropertyChanged -= Changed; }
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
