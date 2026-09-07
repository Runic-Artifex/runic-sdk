using System.Diagnostics;

namespace Runic.Desktop.Tests;

internal static class BrowserTestProfile
{
    public static async Task DeleteAsync(string path, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Waiting for the main Chromium process does not wait for all children
                // to release their Windows file handles. Retry only this owned profile.
                var remaining = timeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) throw;
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(50)
                    ? remaining : TimeSpan.FromMilliseconds(50));
            }
        }
    }
}
