using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Tool;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

/// <summary>WIN-06: an IDE that cannot signal <c>dotnet runic dev</c> asks it to stop on its standard input.</summary>
internal sealed partial class ToolTests
{
    [Test, DisplayName("the stop request on standard input is opt-in through RUNIC_DEV_STOP_ON_INPUT")]
    public void StandardInputStopIsOptIn()
    {
        static Func<string, string?> With(string? value) =>
            name => name == StandardInputStop.Variable ? value : null;
        foreach (string value in new[] { "1", "true", "TRUE", " 1 " })
            True(StandardInputStop.IsRequested(With(value)), $"'{value}' did not turn the stop request on.");
        foreach (string? value in new[] { null, "", "0", "false", "yes" })
            False(StandardInputStop.IsRequested(With(value)), $"'{value}' turned the stop request on.");
    }

    [Test, DisplayName("dev stops at a stop line or at the end of its standard input, once")]
    public void StandardInputStopReadsStopOrEnd()
    {
        List<string> Requests(string input)
        {
            var requests = new List<string>();
            StandardInputStop.Watch(new StringReader(input), requests.Add);
            return requests;
        }

        // Other lines, such as a stray Enter, are ignored; the line is compared without case and surrounding space.
        SequenceEqual([StandardInputStop.StopLineMessage], Requests("\nhello\n  Stop \r\nstop\n").ToArray());
        SequenceEqual([StandardInputStop.StopLineMessage], Requests("stop").ToArray());
        // The IDE closed the pipe, or dev's input was empty from the start.
        SequenceEqual([StandardInputStop.EndMessage], Requests("restart\n").ToArray());
        SequenceEqual([StandardInputStop.EndMessage], Requests(string.Empty).ToArray());
        // A broken pipe ends the input too.
        SequenceEqual([StandardInputStop.EndMessage], RequestsFrom(new BrokenReader()).ToArray());

        List<string> RequestsFrom(TextReader reader)
        {
            var requests = new List<string>();
            StandardInputStop.Watch(reader, requests.Add);
            return requests;
        }
    }

    [Test, DisplayName("the standard input watch runs beside dev and reports a request from a pipe")]
    public async Task StandardInputStopWatchesInTheBackground()
    {
        using var pipe = new AnonymousPipeServerStreamPair();
        var requested = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        StandardInputStop.Start(pipe.Reader, message => requested.TrySetResult(message));
        await Task.Delay(50);
        False(requested.Task.IsCompleted, "dev stopped before the IDE asked.");
        await pipe.Writer.WriteLineAsync("stop");
        await pipe.Writer.FlushAsync();
        Equal(StandardInputStop.StopLineMessage, await requested.Task.WaitAsync(TestTimeouts.Scale(TimeSpan.FromSeconds(10))));
    }

    private sealed class BrokenReader : TextReader
    {
        public override string? ReadLine() => throw new IOException("The pipe has been ended.");
    }

    /// <summary>A connected pipe, as an IDE's redirected standard input is.</summary>
    private sealed class AnonymousPipeServerStreamPair : IDisposable
    {
        private readonly System.IO.Pipes.AnonymousPipeServerStream _server = new(System.IO.Pipes.PipeDirection.Out);
        private readonly System.IO.Pipes.AnonymousPipeClientStream _client;

        internal AnonymousPipeServerStreamPair()
        {
            _client = new(System.IO.Pipes.PipeDirection.In, _server.ClientSafePipeHandle);
            Writer = new StreamWriter(_server);
            Reader = new StreamReader(_client);
        }

        internal StreamWriter Writer { get; }

        internal StreamReader Reader { get; }

        public void Dispose()
        {
            Writer.Dispose();
            Reader.Dispose();
        }
    }
}
