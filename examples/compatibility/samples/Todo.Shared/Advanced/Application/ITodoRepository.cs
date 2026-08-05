using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RunicToolkit.Samples.AdvancedTodo.Domain;

namespace RunicToolkit.Samples.AdvancedTodo.Application;

internal interface ITodoRepository : IDisposable
{
    ValueTask<IReadOnlyList<TodoItem>> LoadAsync(CancellationToken cancellationToken);

    ValueTask SaveAsync(IReadOnlyList<TodoItem> items, CancellationToken cancellationToken);
}
