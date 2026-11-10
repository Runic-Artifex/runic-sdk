using Runic.Desktop;

await using var host = await DesktopHost.StartAsync();
await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
{
    Content = new DesktopContent.Handler(static (request, _) => ValueTask.FromResult<ContentResponse?>(
        request.Path == "/probe" ? ContentResponse.Text("package-ok", "text/plain") : null)),
});

using var client = new HttpClient();
var result = await client.GetStringAsync(new Uri(surface.Url, "probe"));
if (result != "package-ok")
{
    throw new InvalidOperationException($"Unexpected package-consumer response: {result}");
}

#if RUNIC_CONSUMER_NATIVE
// The application's own static library, linked beside the native server (native/).
var answer = ConsumerNative.Answer();
if (answer != 42)
{
    throw new InvalidOperationException($"Unexpected answer from the consumer's native library: {answer}");
}
Console.WriteLine($"The consumer's native library answered {answer}");
#endif

Console.WriteLine($"Runic.Desktop package consumer passed at {surface.Url}");

#if RUNIC_CONSUMER_NATIVE
internal static class ConsumerNative
{
    [System.Runtime.InteropServices.DllImport("consumer_native", EntryPoint = "consumer_native_answer")]
    internal static extern int Answer();
}
#endif
