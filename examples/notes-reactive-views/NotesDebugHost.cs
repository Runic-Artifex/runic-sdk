using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Runic.Application.Views;

namespace NotesReactiveViews;

internal sealed class NotesDebugHost : IDisposable
{
    private readonly Process _frontend;
    private readonly NotesWindow _window;
    private readonly string _profilePath;
    private bool _disposed;
    private bool _windowRecorded;

    private NotesDebugHost(Process frontend, NotesWindow window, string url, string profilePath)
    {
        _frontend = frontend;
        _window = window;
        _profilePath = profilePath;
        Url = url;
        RunicBridgeHotReload.RestartRequired += OnRestartRequired;
    }

    public string Url { get; }

    public void RecordWindow()
    {
        var sessionPath = Path.Combine(FindProjectDirectory(), "obj", "runic-ide-session.json");
        File.WriteAllText(sessionPath, JsonSerializer.Serialize(new { pid = Environment.ProcessId, url = Url, profilePath = _profilePath }));
        _windowRecorded = true;
    }

    public static async Task<NotesDebugHost> StartAsync(NotesWindow window, string framework, bool openWindow)
    {
        var project = FindProjectDirectory();
        var frontend = framework switch
        {
            "angular" => Path.Combine(project, "Angular"),
            "svelte" => Path.Combine(project, "Svelte"),
            "typescript" => Path.Combine(project, "Frontend"),
            _ => throw new ArgumentException("RUNIC_DEV_FRONTEND must be angular, svelte, or typescript.", nameof(framework))
        };
        if (!File.Exists(Path.Combine(frontend, "package.json")))
            throw new DirectoryNotFoundException($"Frontend project not found: {frontend}");

        var profilePath = openWindow
            ? Path.Combine(project, "obj", "runic-ide-browser", $"{Environment.ProcessId}-{Guid.NewGuid():N}")
            : string.Empty;
        if (openWindow)
        {
            Directory.CreateDirectory(profilePath);
            window.NativeWindow.SetProfile("Runic Notes IDE", profilePath);
        }
        var backendUrl = window.StartServer("index.html");
        var frontendPort = AvailablePort();
        var frontendUrl = $"http://127.0.0.1:{frontendPort}/";
        var script = Path.Combine(AppContext.BaseDirectory, "notes-ide-frontend.mjs");
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = frontend
        };
        foreach (var argument in new[] { script, Environment.ProcessId.ToString(), frontend, framework,
                     backendUrl, frontendPort.ToString(), profilePath })
            start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) Console.WriteLine(eventArgs.Data); };
        process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) Console.Error.WriteLine(eventArgs.Data); };
        if (!process.Start()) throw new InvalidOperationException("Could not start the frontend dev process.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var host = new NotesDebugHost(process, window, frontendUrl, profilePath);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (process.HasExited)
                    throw new InvalidOperationException($"The frontend dev process exited with code {process.ExitCode}.");
                try
                {
                    // The WebUI script request reserves its single client slot for the real window.
                    using var response = await client.GetAsync(frontendUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"RUNIC_IDE_READY|{frontendUrl}|backend={backendUrl}|framework={framework}");
                        return host;
                    }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(200);
            }
            throw new TimeoutException($"Frontend dev server did not become ready at {frontendUrl}.");
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RunicBridgeHotReload.RestartRequired -= OnRestartRequired;
        if (!_frontend.HasExited)
        {
            try { _frontend.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* It exited during disposal. */ }
            _frontend.WaitForExit(3000);
        }
        _frontend.Dispose();
        if (!_windowRecorded && Directory.Exists(_profilePath))
            Directory.Delete(_profilePath, recursive: true);
    }

    private void OnRestartRequired(string mismatch)
    {
        const string notice = "Bridge contract changed. Restart debugging to rebuild the bridge and reconnect the view.";
        try
        {
            _window.NativeWindow.RunJavaScript($$"""
                (() => {
                  const id = 'runic-ide-contract-restart';
                  let notice = document.getElementById(id);
                  if (!notice) {
                    notice = document.createElement('aside');
                    notice.id = id;
                    notice.setAttribute('role', 'alert');
                    notice.style.cssText = 'position:fixed;z-index:2147483647;left:16px;right:16px;bottom:16px;padding:16px 20px;background:#45240b;color:#fff8ea;border:2px solid #f5bc61;border-radius:8px;box-shadow:0 8px 30px #0008;font:600 15px system-ui,sans-serif';
                    document.body.appendChild(notice);
                  }
                  notice.textContent = {{JsonSerializer.Serialize(notice)}};
                })();
                """);
        }
        catch (Exception error) { Trace.TraceWarning($"Could not show Bridge restart notice: {error}"); }
    }

    private static string FindProjectDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "NotesReactiveViews.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not find the Reactive Notes source project for IDE development.");
    }

    private static int AvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
