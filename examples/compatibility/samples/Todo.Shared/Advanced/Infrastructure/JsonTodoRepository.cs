using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.Samples.AdvancedTodo.Application;
using WebUIToolkit.Samples.AdvancedTodo.Domain;

namespace WebUIToolkit.Samples.AdvancedTodo.Infrastructure;

internal sealed class JsonTodoRepository : ITodoRepository
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal JsonTodoRepository(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async ValueTask<IReadOnlyList<TodoItem>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            await using FileStream input = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync(
                    input,
                    AdvancedTodoJsonContext.Default.ListTodoItem,
                    cancellationToken)
                .ConfigureAwait(false) ?? [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(
        IReadOnlyList<TodoItem> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = _path + ".new";
            await using (FileStream output = new(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                        output,
                        items,
                        AdvancedTodoJsonContext.Default.IReadOnlyListTodoItem,
                        cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
