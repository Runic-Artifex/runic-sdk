using System.Diagnostics;
using Runic.Desktop.Internal;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Tests;

public sealed partial class ShutdownSignalTests
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ShutdownRequestClosesOpenWindowsWithoutDisposingTheHost()
    {
        var factory = new ClosableWindowHostFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            WaitForConnection = false,
        });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("x") });
        var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            // A signal is not a user action: it closes without asking.
            ConfirmCloseAsync = static _ => ValueTask.FromResult(false),
        });

        Assert.True(host.RequestShutdown("test"));
        Assert.False(host.RequestShutdown("test"));
        await Task.Run(() => window.WaitForClose()).WaitAsync(StepTimeout);

        Assert.True(host.ShutdownRequested.IsCancellationRequested);
        Assert.False(window.IsOpen);
        Assert.True(factory.Host!.Disposed);
        // The application's owner still disposes the host.
        Assert.True(host.Validate().IsValid);
    }

    [Fact]
    public async Task WindowOpenedAfterShutdownRequestClosesAtOnce()
    {
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new ClosableWindowHostFactory(),
            WaitForConnection = false,
        });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("x") });
        host.RequestShutdown("test");

        var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });

        Assert.False(window.IsOpen);
        await Task.Run(() => window.WaitForClose()).WaitAsync(StepTimeout);
    }

    [Theory]
    [InlineData("SIGINT")]
    [InlineData("SIGTERM")]
    public async Task SignalClosesTheWindowDrainsWorkAndDisposesTheHost(string signal)
    {
        // Windows has no SIGTERM to send to a console application; its Ctrl+C case covers the handler.
        if (OperatingSystem.IsWindows() && signal != "SIGINT") return;
        await using var fixture = Fixture.Start("orderly");
        await fixture.WaitForAsync("ready");

        fixture.Send(signal);

        await fixture.WaitForAsync("shutdown-requested");
        await fixture.WaitForAsync("window-closed");
        await fixture.WaitForAsync("work-completed");
        await fixture.WaitForAsync("work-drained");
        await fixture.WaitForAsync("host-disposed");
        Assert.Equal(ShutdownSignalFixture.ApplicationExitCode, await fixture.WaitForExitAsync());
    }

    [Fact]
    public async Task OptOutKeepsTheDefaultSignalAction()
    {
        await using var fixture = Fixture.Start("opt-out");
        await fixture.WaitForAsync("ready");

        fixture.Send("SIGINT");

        Assert.NotEqual(ShutdownSignalFixture.ApplicationExitCode, await fixture.WaitForExitAsync());
        Assert.DoesNotContain("shutdown-requested", fixture.Output);
        Assert.DoesNotContain("host-disposed", fixture.Output);
    }

    [Theory]
    [InlineData("SIGINT")]
    [InlineData("SIGTERM")]
    public async Task ForwardedCopiesOfTheSignalDoNotEndTheShutdown(string repeat)
    {
        // A terminal Ctrl+C under dotnet watch reaches the app, then watch sends SIGTERM. Windows has no SIGTERM to
        // send, and its console delivers one Ctrl+C per key press.
        if (OperatingSystem.IsWindows()) return;
        await using var fixture = Fixture.Start("orderly");
        await fixture.WaitForAsync("ready");

        fixture.Send("SIGINT");
        fixture.Send(repeat);

        await fixture.WaitForAsync("host-disposed");
        Assert.Equal(ShutdownSignalFixture.ApplicationExitCode, await fixture.WaitForExitAsync());
    }

    [Fact]
    public async Task SecondSignalEndsAShutdownThatDoesNotFinish()
    {
        await using var fixture = Fixture.Start("stuck");
        await fixture.WaitForAsync("ready");

        fixture.Send("SIGINT");
        await fixture.WaitForAsync("shutdown-requested");
        await Task.Delay(ShutdownSignals.RepeatWindow + TimeSpan.FromMilliseconds(200));
        fixture.Send("SIGINT");

        Assert.NotEqual(ShutdownSignalFixture.ApplicationExitCode, await fixture.WaitForExitAsync());
        Assert.DoesNotContain("host-disposed", fixture.Output);
    }

    private sealed partial class Fixture : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly List<string> _lines = [];
        private readonly SemaphoreSlim _received = new(0);
        private readonly Task _reader;

        private Fixture(Process process)
        {
            _process = process;
            _reader = Task.Run(ReadAsync);
        }

        internal IReadOnlyList<string> Output
        {
            get { lock (_lines) return [.. _lines]; }
        }

        internal static Fixture Start(string mode)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                // On Windows this gives the fixture its own hidden console, so its Ctrl+C reaches no other process.
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(typeof(ShutdownSignalFixture).Assembly.Location);
            start.ArgumentList.Add(ShutdownSignalFixture.Argument);
            start.ArgumentList.Add(mode);
            return new Fixture(Process.Start(start) ?? throw new InvalidOperationException("The fixture did not start."));
        }

        internal async Task WaitForAsync(string step)
        {
            using var timeout = new CancellationTokenSource(StepTimeout);
            while (true)
            {
                lock (_lines)
                {
                    if (_lines.Contains(step)) return;
                }
                try
                {
                    await _received.WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException($"The fixture did not report '{step}'. Output: {string.Join(" | ", Output)}");
                }
            }
        }

        internal void Send(string signal)
        {
            if (OperatingSystem.IsWindows())
            {
                _process.StandardInput.WriteLine("ctrl-c");
                _process.StandardInput.Flush();
                return;
            }
            // Signal numbers shared by Linux and macOS.
            var number = signal switch { "SIGINT" => 2, "SIGTERM" => 15, _ => throw new ArgumentOutOfRangeException(nameof(signal)) };
            if (Kill(_process.Id, number) != 0)
                throw new InvalidOperationException($"kill({_process.Id}, {signal}) failed with errno {Marshal.GetLastPInvokeError()}.");
        }

        internal async Task<int> WaitForExitAsync()
        {
            using var timeout = new CancellationTokenSource(StepTimeout);
            await _process.WaitForExitAsync(timeout.Token);
            await _reader;
            return _process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            await _reader;
            _process.Dispose();
            _received.Dispose();
        }

        private async Task ReadAsync()
        {
            var errors = _process.StandardError.ReadToEndAsync();
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                lock (_lines) _lines.Add(line);
                _received.Release();
            }
            var error = await errors;
            if (error.Length > 0)
            {
                lock (_lines) _lines.Add($"stderr: {error}");
            }
            _received.Release();
        }

        [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static partial int Kill(int processId, int signal);
    }
}
