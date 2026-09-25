using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Signals;
using Runic.Application.Views;

namespace Runic.Application.Views.ReactiveUI;

/// <summary>ReactiveUI command execution waits for observable completion and errors.</summary>
public static class ReactiveCommandDescriptors
{
    public static CommandDescriptor<TViewModel> Async<TViewModel>(
        string name, Func<TViewModel, ReactiveCommand<RxVoid, RxVoid>> get) =>
        new(name, get, async (viewModel, token, _) =>
        {
            await Signal.ToTask(get(viewModel).Execute(), token).ConfigureAwait(false);
        });
}
