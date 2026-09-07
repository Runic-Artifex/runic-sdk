using System.Net;
using System.Net.Sockets;
using System.Text;
using Runic.Application.CsWebUi;

internal static class NativeServerReadinessTests
{
    internal static async Task RunAsync()
    {
        await WaitsForListenerAndHttpHandlerAsync();
        await BoundsStartupAndPreservesCancellationAsync();
        Console.WriteLine("CS-WebUI readiness: delayed listener, HTTP handler, timeout and cancellation passed.");
    }

    private static async Task WaitsForListenerAndHttpHandlerAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = BoundSocket();
        var url = Address(listener);
        // Reserve the address without listening, matching native WebUI's
        // published URL before the server thread reaches mg_start.
        Task ready = NativeServerReadiness.WaitAsync(url, TimeSpan.FromSeconds(5), deadline.Token);
        await Task.Delay(50, deadline.Token);
        Check(!ready.IsCompleted, "An unopened listener cannot be ready.");
        listener.Listen(1);

        foreach (var status in new[] { "HTTP/1.1 503 Unavailable", "HTTP/1.1 200XInvalid" })
        {
            using var connection = await listener.AcceptAsync(deadline.Token);
            using var stream = new NetworkStream(connection);
            await ReadRequestAsync(stream, deadline.Token);
            Check(!ready.IsCompleted, "A listening socket alone cannot be ready.");
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"{status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), deadline.Token);
        }

        using var accepted = await listener.AcceptAsync(deadline.Token);
        using var response = new NetworkStream(accepted);
        await ReadRequestAsync(response, deadline.Token);
        Check(!ready.IsCompleted, "An error or malformed HTTP status cannot be ready.");
        await response.WriteAsync("HTTP/1.1 2"u8.ToArray(), deadline.Token);
        await response.WriteAsync("00 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), deadline.Token);
        await ready;
    }

    private static async Task BoundsStartupAndPreservesCancellationAsync()
    {
        using var listener = BoundSocket();
        var url = Address(listener);
        var failure = await ExpectAsync<TimeoutException>(NativeServerReadiness.WaitAsync(url, TimeSpan.FromMilliseconds(100), default));
        Check(failure.Message.Contains(url.ToString(), StringComparison.Ordinal), "Timeout must identify the server.");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await ExpectAsync<OperationCanceledException>(NativeServerReadiness.WaitAsync(url, TimeSpan.FromSeconds(5), cancellation.Token));

        // Connecting succeeds, but a server that never responds must also time out.
        listener.Listen(1);
        Task stalled = NativeServerReadiness.WaitAsync(url, TimeSpan.FromMilliseconds(500), default);
        using var accepted = await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await ExpectAsync<TimeoutException>(stalled);
    }

    private static Socket BoundSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static Uri Address(Socket listener) => new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndPoint!).Port}/.runic-ready/test");

    private static async Task ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        Check(await reader.ReadLineAsync(cancellationToken) == "GET /.runic-ready/test HTTP/1.1", "Probe must request the readiness route without a browser session.");
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 }) { }
    }

    private static async Task<T> ExpectAsync<T>(Task operation) where T : Exception
    {
        try { await operation; }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
