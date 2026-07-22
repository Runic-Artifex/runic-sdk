using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.CommandLine.Processes.Tests;

internal static class ChildProcessFixture
{
    private const string ChildSwitch = "--process-test-child";

    public static bool IsChildInvocation(string[] args)
    {
        return args.Length >= 2 && string.Equals(args[0], ChildSwitch, StringComparison.Ordinal);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        return args[1] switch
        {
            "echo-arguments" => EchoArguments(args.AsSpan(2)),
            "pressure" => await WritePressureAsync(args).ConfigureAwait(false),
            "sleep" => await SleepAsync(args).ConfigureAwait(false),
            "tree-parent" => await RunTreeParentAsync(args).ConfigureAwait(false),
            "tree-leaf" => await RunTreeLeafAsync(args).ConfigureAwait(false),
            _ => 97,
        };
    }

    public static string ExecutablePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("The executable path is unavailable.");

    public static string[] CreateArguments(string mode, params string[] arguments)
    {
        var result = new string[arguments.Length + 2];
        result[0] = ChildSwitch;
        result[1] = mode;
        arguments.CopyTo(result, 2);
        return result;
    }

    private static int EchoArguments(ReadOnlySpan<string> arguments)
    {
        Console.WriteLine(arguments.Length.ToString(CultureInfo.InvariantCulture));

        foreach (string argument in arguments)
        {
            Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(argument)));
        }

        return 0;
    }

    private static async Task<int> WritePressureAsync(string[] args)
    {
        int standardOutputBytes = ParseNonNegativeInt32(args, 2);
        int standardErrorBytes = ParseNonNegativeInt32(args, 3);
        int exitCode = ParseNonNegativeInt32(args, 4);
        int chunkSize = args.Length > 5 ? ParsePositiveInt32(args, 5) : 4096;

        Task output = WriteBytesAsync(Console.OpenStandardOutput(), (byte)'O', standardOutputBytes, chunkSize);
        Task error = WriteBytesAsync(Console.OpenStandardError(), (byte)'E', standardErrorBytes, chunkSize);
        await Task.WhenAll(output, error).ConfigureAwait(false);
        return exitCode;
    }

    private static async Task<int> SleepAsync(string[] args)
    {
        int delayMilliseconds = ParseNonNegativeInt32(args, 2);
        int exitCode = args.Length > 3 ? ParseNonNegativeInt32(args, 3) : 0;
        await Task.Delay(delayMilliseconds).ConfigureAwait(false);
        return exitCode;
    }

    private static async Task<int> RunTreeParentAsync(string[] args)
    {
        string markerPath = args[2];
        string delayMilliseconds = args[3];
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add(ChildSwitch);
        process.StartInfo.ArgumentList.Add("tree-leaf");
        process.StartInfo.ArgumentList.Add(markerPath);
        process.StartInfo.ArgumentList.Add(delayMilliseconds);

        if (!process.Start())
        {
            return 96;
        }

        await File.WriteAllTextAsync(
            string.Concat(markerPath, ".started"),
            process.Id.ToString(CultureInfo.InvariantCulture),
            Encoding.UTF8).ConfigureAwait(false);
        Console.WriteLine("READY");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task<int> RunTreeLeafAsync(string[] args)
    {
        string markerPath = args[2];
        int delayMilliseconds = ParseNonNegativeInt32(args, 3);
        await Task.Delay(delayMilliseconds).ConfigureAwait(false);
        await File.WriteAllTextAsync(markerPath, "survived", Encoding.UTF8).ConfigureAwait(false);
        return 0;
    }

    private static async Task WriteBytesAsync(Stream stream, byte value, int count, int chunkSize)
    {
        byte[] buffer = new byte[Math.Min(chunkSize, Math.Max(count, 1))];
        Array.Fill(buffer, value);

        while (count > 0)
        {
            int bytesToWrite = Math.Min(count, buffer.Length);
            await stream.WriteAsync(buffer.AsMemory(0, bytesToWrite)).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            count -= bytesToWrite;
        }
    }

    private static int ParseNonNegativeInt32(string[] args, int index)
    {
        int value = int.Parse(args[index], NumberStyles.None, CultureInfo.InvariantCulture);
        return value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(args));
    }

    private static int ParsePositiveInt32(string[] args, int index)
    {
        int value = ParseNonNegativeInt32(args, index);
        return value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(args));
    }
}
