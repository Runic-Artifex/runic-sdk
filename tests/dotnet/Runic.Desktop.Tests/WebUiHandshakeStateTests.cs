using TUnit.Core;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// The handshake state transitions, driven directly so that both orderings of a token
// check and a deadline expiry are exercised without timers (#35).
public sealed class WebUiHandshakeStateTests
{
    [Test]
    public void ATokenCheckBeforeTheDeadlineKeepsTheSocket()
    {
        var handshake = new WebUiHandshake();

        Assert.Equal(WebUiHandshake.Pending, handshake.CheckToken(tokenIsValid: true));
        Assert.False(handshake.TryExpire());
        Assert.Equal(WebUiHandshake.TokenChecked, handshake.State);
        Assert.False(handshake.IsClosing);
    }

    [Test]
    public void AnExpiryBeforeTheTokenCheckClosesTheSocketAndTheCheckCannotRevertIt()
    {
        var handshake = new WebUiHandshake();

        Assert.True(handshake.TryExpire());
        Assert.Equal(WebUiHandshake.Expired, handshake.CheckToken(tokenIsValid: true));
        Assert.Equal(WebUiHandshake.Expired, handshake.State);
        Assert.True(handshake.IsClosing);
    }

    [Test]
    public void AnEndedSocketCannotBeExpired()
    {
        var handshake = new WebUiHandshake();

        Assert.True(handshake.TryEnd());
        Assert.False(handshake.TryExpire());
        Assert.Equal(WebUiHandshake.Ended, handshake.State);
        Assert.Equal(WebUiHandshake.Ended, handshake.CheckToken(tokenIsValid: true));
    }

    [Test]
    public void AnExpiredSocketCannotEnd()
    {
        var handshake = new WebUiHandshake();

        Assert.True(handshake.TryExpire());
        Assert.False(handshake.TryEnd());
        Assert.Equal(WebUiHandshake.Expired, handshake.State);
    }

    [Test]
    public void AWrongTokenRejectsThePendingSocketPermanently()
    {
        var handshake = new WebUiHandshake();

        Assert.Equal(WebUiHandshake.Pending, handshake.CheckToken(tokenIsValid: false));
        Assert.Equal(WebUiHandshake.Rejected, handshake.State);
        Assert.True(handshake.IsClosing);
        Assert.Equal(WebUiHandshake.Rejected, handshake.CheckToken(tokenIsValid: true));
        Assert.False(handshake.TryExpire());
        Assert.Equal(WebUiHandshake.Rejected, handshake.State);
    }

    [Test]
    public void AWrongTokenAfterAMatchRejectsAnAuthenticatedSocket()
    {
        var handshake = new WebUiHandshake();

        Assert.Equal(WebUiHandshake.Pending, handshake.CheckToken(tokenIsValid: true));
        Assert.Equal(WebUiHandshake.TokenChecked, handshake.CheckToken(tokenIsValid: true));
        Assert.Equal(WebUiHandshake.TokenChecked, handshake.CheckToken(tokenIsValid: false));
        Assert.Equal(WebUiHandshake.Rejected, handshake.State);
    }

    // I-081 (1). Counted connections that no session in the table stands for, such as an
    // older socket that is closing and has left the table but is not counted down yet,
    // keep the connection taken. A silent socket must not be closed for a newer one that
    // is turned away anyway; it yields once the connection can be freed.
    [Test]
    public async Task ASilentSocketYieldsOnlyWhenThatFreesTheConnection()
    {
        await using var window = new WebUiWindow();
        await using var silent = await SilentSessionAsync(window, new MemoryStream());

        Assert.False(WebUiWindow.YieldSilentConnections(activeConnections: 2, [silent]));
        Assert.False(silent.IsHandshakeClosing);

        Assert.True(WebUiWindow.YieldSilentConnections(activeConnections: 1, [silent]));
        Assert.True(silent.IsHandshakeClosing);
    }

    // I-081 (2). A socket that ended before its token check has no callbacks or disconnect
    // to finish, so it no longer holds the connection while it is removed.
    [Test]
    public async Task ASocketThatEndedBeforeItsTokenCheckGivesUpTheConnection()
    {
        await using var window = new WebUiWindow();
        // The empty stream ends the socket at its first receive.
        await using var ended = new WebUiSession(window, Socket(new MemoryStream()), 1, 1, string.Empty, TimeSpan.FromMinutes(1),
            TimeProvider.System);
        await Assert.ThrowsAsync<WebSocketException>(() => ended.RunAsync(CancellationToken.None));

        Assert.True(WebUiWindow.YieldSilentConnections(activeConnections: 1, [ended]));
    }

    // I-081 (3). Expiring a silent socket logs on the thread pool. A logger that throws
    // must not keep the server from closing the socket.
    [Test]
    public async Task AnExpiredSocketIsClosedWhenTheLoggerThrows()
    {
        var options = new PresentationSurfaceRuntimeOptions(null, null, new EmbeddedHostTests.RecordingHostFactory(),
            WaitForConnection: false, TimeSpan.FromSeconds(10), DiagnosticSink: null, Logger: new ThrowingLogger());
        await using var window = new WebUiWindow(host: null, pathBase: string.Empty, runtimeOptions: options);
        var stream = new MemoryStream();
        var socket = Socket(stream);
        await using var silent = await SilentSessionAsync(window, socket);

        Assert.True(silent.TryYieldSilentHandshake());
        Assert.True(SpinWait.SpinUntil(() => socket.State == WebSocketState.CloseSent, TestTimeouts.Scale(TimeSpan.FromSeconds(10))),
            $"The expired socket was not closed; it is {socket.State}.");
    }

    private static WebSocket Socket(Stream stream) =>
        WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });

    private static Task<WebUiSession> SilentSessionAsync(WebUiWindow window, Stream stream) =>
        SilentSessionAsync(window, Socket(stream));

    // A socket that never sent its token check and is past the yield grace period (here
    // the 1 ms handshake timeout, on the test's clock). Its receive loop is not started, so no deadline runs.
    private static Task<WebUiSession> SilentSessionAsync(WebUiWindow window, WebSocket socket)
    {
        var time = new ManualTime();
        var session = new WebUiSession(window, socket, 1, 1, string.Empty, TimeSpan.FromMilliseconds(1), time);
        time.Advance(TimeSpan.FromMilliseconds(1));
        return Task.FromResult(session);
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("The logger failed.");
    }
}
