using System.ComponentModel;
using ReactiveUI;
using ReactiveUI.Primitives.Concurrency;

namespace Runic.Application.Testing.Tests;

// W260-015: ViewModels that push a background value onto ReactiveUI's main-thread scheduler outside a model turn,
// as after Task.Run or ConfigureAwait(false). Event 1043 names the type that declares the scheduling code, once per
// type, so each case uses its own.
internal sealed class OpenedWindowOffTurnViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public static Task PushOutsideTurnAsync() => Task.Run(() =>
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = RxSchedulers.MainThreadScheduler.Schedule(done, static done => done.TrySetResult());
        return done.Task;
    });
}

internal sealed class TestHostOffTurnViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public static Task PushOutsideTurnAsync() => Task.Run(() =>
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = RxSchedulers.MainThreadScheduler.Schedule(done, static done => done.TrySetResult());
        return done.Task;
    });
}
