using System;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.DependencyNotices.Tool;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += handler;
        try
        {
            return await ToolApplication.RunAsync(args, Console.Out, Console.Error, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
}
