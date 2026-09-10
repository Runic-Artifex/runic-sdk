using Runic.Desktop;
using Runic.Desktop.Gtk4;
using Runic.Platform;
using Runic.Platform.Linux.Gtk4;
using Runic.Platform.Linux.Portal;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

[SupportedOSPlatform("linux")]
internal static class UsabilitySmoke
{
    internal static int Run() => Gtk4Application.Run(async () =>
    {
        CheckSandboxDenial();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        var factory = new CaptureFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk4WebKit6 },
            WindowHostFactory = factory, WaitForConnection = true,
        });
        await using var surface = await host.CreateSurfaceAsync(new() { Content = Page });
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded, Width = 900, Height = 760 });
        var owner = new Gtk4PortalWindowOwner(new HostOwner(factory.Host!));
        var app = new PortalApplication(Environment.GetEnvironmentVariable("RUNIC_TEST_APP_ID") ?? "com.runic.tests.Activation", Console.WriteLine);
        var picker = app.CreateFileDialogs(owner);
        IDesktopInhibitionLease? inhibition = null;
        try
        {
            Console.WriteLine($"USABILITY READY pid={Environment.ProcessId}");
            while (!deadline.IsCancellationRequested && window.IsOpen)
            {
                string state = await surface.ExecuteJavaScriptAsync("return JSON.stringify(window.takeState());", TimeSpan.FromSeconds(5));
                using var json = JsonDocument.Parse(state);
                var action = json.RootElement.GetProperty("action").GetString();
                if (action is { Length: > 0 })
                {
                    Console.WriteLine("USABILITY " + state);
                    string result = "";
                    if (action == "done") break;
                    if (action == "open")
                    {
                        var selected = await picker.OpenFileAsync(new(), deadline.Token);
                        if (selected is PickerResult<IReadFileLease>.Selected file)
                        {
                            await using var lease = file.Value;
                            await using var stream = await lease.OpenReadAsync(deadline.Token);
                            using var reader = new StreamReader(stream);
                            char[] buffer = new char[256];
                            int count = await reader.ReadAsync(buffer.AsMemory(), deadline.Token);
                            result = "Opened " + lease.DisplayName + ": " + new string(buffer, 0, count);
                            CheckSandboxDenial();
                        }
                        else result = selected.ToString()!;
                    }
                    else if (action == "save")
                    {
                        var selected = await picker.SaveFileAsync(new("runic-usability.txt"), deadline.Token);
                        if (selected is PickerResult<ISaveFileLease>.Selected file)
                        {
                            await using var lease = file.Value;
                            result = (await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace, deadline.Token)).ToString()!;
                        }
                        else result = selected.ToString()!;
                    }
                    else if (action == "inhibit")
                    {
                        if (inhibition is not null) continue;
                        var acquired = await app.CreateInhibition(owner).AcquireAsync(DesktopInhibitionEffects.SystemSleep | DesktopInhibitionEffects.DisplaySleep, "Runic test export", deadline.Token);
                        if (acquired is PlatformResult<IDesktopInhibitionLease>.Success success) { inhibition = success.Value; result = "Inhibition request held. Click Release inhibition to end the operation."; }
                        else result = acquired.ToString()!;
                    }
                    else if (action == "release")
                    {
                        if (inhibition is not null) await inhibition.DisposeAsync();
                        inhibition = null;
                        result = "Inhibition released.";
                    }
                    else if (action == "close-owner")
                    {
                        var pending = picker.OpenFileAsync(new(), deadline.Token).AsTask();
                        await Task.Delay(750, deadline.Token);
                        await window.CloseAsync();
                        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                        Console.WriteLine("OWNER CLOSE RESULT " + outcome);
                        if (outcome is not PickerResult<IReadFileLease>.Unavailable { Reason: UnavailableReason.OwnerClosed })
                            throw new InvalidOperationException("Closing the picker owner did not invalidate the request.");
                        break;
                    }
                    else if (action == "snapshot") result = "Snapshot recorded.";
                    Console.WriteLine("RESULT " + result);
                    await surface.ExecuteJavaScriptAsync("document.getElementById('result').textContent=" + JsonSerializer.Serialize(result, UsabilityJsonContext.Default.String) + "; return true;", TimeSpan.FromSeconds(5));
                }
                await Task.Delay(200, deadline.Token);
            }
        }
        finally { if (inhibition is not null) await inhibition.DisposeAsync(); }
        Console.WriteLine("USABILITY session ended; inspect individual evidence before marking checks passed.");
        return 0;
    });

    private static void CheckSandboxDenial()
    {
        if (Environment.GetEnvironmentVariable("RUNIC_TEST_DENIED_FILE") is not { Length: > 0 } path) return;
        try { _ = File.ReadAllText(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            Console.WriteLine("SANDBOX PASS direct access to the host sentinel is denied: " + error.GetType().Name);
            return;
        }
        throw new InvalidOperationException("Sandbox unexpectedly permits direct access to the host sentinel.");
    }

    private sealed class CaptureFactory : IDesktopWindowHostFactory
    {
        private readonly Gtk4WindowHostFactory _factory = new();
        internal IDesktopNativeDispatchWindowHost? Host;
        public bool IsSupported => _factory.IsSupported;
        public DesktopWindowCapabilities Capabilities => _factory.Capabilities;
        public IDesktopWindowHost Create() => Host = (IDesktopNativeDispatchWindowHost)_factory.Create();
    }

    private const string Page = """
<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Runic GTK4 usability</title><script src="webui.js"></script>
<style>body{font:18px sans-serif;max-width:48rem;margin:1.5rem auto;padding:0 1rem;color:#18222d;background:#f5f7fa}input,textarea,button{font:inherit;padding:.65rem;margin:.35rem 0}input,textarea{box-sizing:border-box;width:100%}button{margin-right:.6rem} :focus-visible{outline:3px solid #125bb3;outline-offset:3px}#result{white-space:pre-wrap}fieldset{margin:1rem 0}#target{min-width:160px;min-height:64px}</style>
<main><h1>Runic GTK4 usability</h1><p>Use Tab and Shift+Tab to navigate. Compose text with your real input method. At each desktop scale, click the target and record a snapshot.</p>
<label for="name">Your name</label><input id="name" autocomplete="off" placeholder="Type a name">
<label for="composition">Composition text</label><textarea id="composition" rows="2" placeholder="Use an IME to enter 你好"></textarea>
<button id="target" onclick="window.hits++;this.textContent='Target hits: '+window.hits">Target hits: 0</button>
<button onclick="act('snapshot')">Record snapshot</button>
<fieldset><legend>Native services</legend><button onclick="act('open')">Open file</button><button onclick="act('save')">Choose save destination</button><button id="inhibit" onclick="this.disabled=true;act('inhibit')">Hold inhibition</button><button onclick="document.getElementById('inhibit').disabled=false;act('release')">Release inhibition</button></fieldset>
<p id="result" role="status" aria-live="polite">Ready.</p><button onclick="act('close-owner')">Close owner during picker</button><button onclick="act('done')">Finish session</button></main>
<script>
window.hits=0;window.events=[];window.action='';
window.act=a=>window.action=a;
for(const name of ['compositionstart','compositionupdate','compositionend','input'])
 document.getElementById('composition').addEventListener(name,e=>{window.events.push({type:e.type,data:e.data,isComposing:e.isComposing,value:e.target.value});if(window.events.length>64)window.events.shift();});
window.takeState=()=>{const state={action:window.action,dpr:devicePixelRatio,width:innerWidth,height:innerHeight,hits:window.hits,name:document.getElementById('name').value,text:document.getElementById('composition').value,events:window.events,focused:document.activeElement?.id};window.action='';return state;};
</script></html>
""";
}

[JsonSerializable(typeof(string))]
internal sealed partial class UsabilityJsonContext : JsonSerializerContext;
