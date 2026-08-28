using Runic.Desktop;

if (!WebUiApplication.EmbeddedWebViewExists)
{
    throw new PlatformNotSupportedException("The platform embedded WebView runtime is not available.");
}

using var window = new WebUiWindow();
window.SetSize(640, 480);
window.SetMinimumSize(320, 240);
window.SetHidden(true);
window.ShowWebView("""
    <!doctype html>
    <html>
    <head>
      <meta charset="utf-8">
      <script src="webui.js"></script>
      <title>Runic Desktop M4 smoke</title>
    </head>
    <body>embedded</body>
    </html>
    """);

if (window.NativeWindowHandle == 0 || window.CurrentBrowser != WebUiBrowser.WebView)
{
    throw new InvalidOperationException("The embedded platform window did not open.");
}
if (window.ExecuteJavaScript("return document.title;", TimeSpan.FromSeconds(10)) != "Runic Desktop M4 smoke")
{
    throw new InvalidOperationException("The embedded WebView bridge did not execute JavaScript.");
}

window.SetSize(700, 500);
window.SetPosition(20, 30);
window.SetHidden(false);
window.Focus();
window.Minimize();
window.Maximize();
window.Close();

if (window.IsShown || window.NativeWindowHandle != 0)
{
    throw new InvalidOperationException("The embedded platform window did not close.");
}

window.SetHidden(true);
window.ShowWebView("""
    <!doctype html><html><head><script src="webui.js"></script><title>restarted</title></head><body></body></html>
    """);
if (window.ExecuteJavaScript("return document.title;", TimeSpan.FromSeconds(10)) != "restarted")
{
    throw new InvalidOperationException("The embedded WebView did not restart.");
}
window.Close();

Console.WriteLine("Runic Desktop embedded WebView smoke passed.");
