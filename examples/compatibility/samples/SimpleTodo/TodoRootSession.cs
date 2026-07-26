using System;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.Hosting.WebUi;

namespace WebUIToolkit.Samples.SimpleTodo;

/// <summary>Connects the sample backend to WebUIToolkit's root-session lifecycle.</summary>
internal sealed class TodoRootSessionFactory(TodoBackend backend) : IRootSessionFactory
{
    public ValueTask<IRootSession> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IRootSession>(new TodoRootSession(backend));
    }

    private sealed class TodoRootSession(TodoBackend backend) : IRootSession
    {
        private bool active;

        public async ValueTask ActivateAsync(CancellationToken cancellationToken)
        {
            if (!active)
            {
                await backend.ActivateAsync(cancellationToken);
                active = true;
            }
        }

        public async ValueTask DeactivateAsync(CancellationToken cancellationToken)
        {
            if (active)
            {
                await backend.DeactivateAsync(cancellationToken);
                active = false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DeactivateAsync(CancellationToken.None);
        }
    }
}
