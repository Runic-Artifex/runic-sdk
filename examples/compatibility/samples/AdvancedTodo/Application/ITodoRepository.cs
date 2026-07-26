using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.Samples.AdvancedTodo.Domain;

namespace WebUIToolkit.Samples.AdvancedTodo.Application;

internal interface ITodoRepository : IDisposable
{
    ValueTask<IReadOnlyList<TodoItem>> LoadAsync(CancellationToken cancellationToken);

    ValueTask SaveAsync(IReadOnlyList<TodoItem> items, CancellationToken cancellationToken);
}
