using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Runic.Desktop.Internal;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// The first Chromium start on a fresh hosted Windows runner is slow: in 640 measured launches per image
// (windows-2025, windows-11-arm), the first Chrome start of each runner took 5 to 24 seconds before its first
// request, and later ones a median of 1.3 to 2.5 seconds (I-180). The browser cases are budgeted for a started
// browser, so pay that first start once here, outside every case's budget. It proves nothing and fails nothing.
public static class BrowserWarmUp
{
    [Before(HookType.Assembly)]
    public static async Task StartEachChromiumOnceAsync()
    {
        var executables = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        try
        {
            if (BrowserBridgeTests.FindChrome() is { } chrome) executables.Add(chrome);
        }
        catch (FileNotFoundException)
        {
        }
        foreach (var browser in new[] { WebUiBrowser.Chrome, WebUiBrowser.Edge, WebUiBrowser.Chromium })
        {
            if (WebUiBrowserDiscovery.Find(browser, customFolder: null) is { IsChromiumBased: true } installation)
            {
                executables.Add(installation.ExecutablePath);
                break;
            }
        }
        foreach (var executable in executables)
        {
            await StartOnceAsync(executable);
        }
    }

    // Starts the browser at a loopback address and stops it once its first request arrives, the moment the cases
    // wait for, or after a minute.
    private static async Task StartOnceAsync(string executable)
    {
        var profile = Directory.CreateTempSubdirectory("runic-desktop-warm-up-");
        var started = Stopwatch.StartNew();
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var startInfo = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[]
            {
                "--headless=new", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage", "--no-first-run",
                $"--user-data-dir={profile.FullName}", $"http://127.0.0.1:{port}/",
            })
            {
                startInfo.ArgumentList.Add(argument);
            }
            using var process = Process.Start(startInfo);
            if (process is null) return;
            _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            string outcome;
            try
            {
                using var accepted = await listener.AcceptTcpClientAsync().WaitScaledAsync(TimeSpan.FromSeconds(60));
                outcome = "requested its page";
            }
            catch (TimeoutException)
            {
                outcome = "requested nothing";
            }
            finally
            {
                try
                {
                    process.Kill(entireProcessTree: !OperatingSystem.IsMacOS());
                    await process.WaitForExitAsync().WaitScaledAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
                {
                }
            }
            Console.WriteLine($"Browser warm-up: {executable} {outcome} after {started.Elapsed.TotalSeconds:0.0} s.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or SocketException)
        {
            Console.WriteLine($"Browser warm-up of {executable} failed: {exception.Message}");
        }
        finally
        {
            try
            {
                await BrowserTestProfile.DeleteAsync(profile.FullName, TimeSpan.FromSeconds(10));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Browser warm-up profile {profile.FullName} remains: {exception.Message}");
            }
        }
    }
}
