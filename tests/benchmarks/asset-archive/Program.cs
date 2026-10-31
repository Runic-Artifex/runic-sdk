using System.Diagnostics;
using System.Globalization;
using Runic.Assets;

// Loads the embedded archive as a Runic host does (FrontendArchive.FromBuild), opens what a first page requests (the
// entry document and the scripts and styles it loads), then every asset, and prints the times, the peak resident memory and
// whether OpenSSL was loaded, as one JSON line.
long start = Stopwatch.GetTimestamp();
AssetArchiveSource source = AssetArchive.ReadEmbedded(typeof(Program).Assembly);
double loaded = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

await ReadAsync(source.Manifest.EntryPoint.RelativePath);
// measure.mjs names the files the entry document loads index-* and vendor-*; lazy chunks, images and fonts are not.
foreach (AssetDescriptor asset in source.Manifest.Assets)
{
    if (asset.RelativePath.StartsWith("assets/index-", StringComparison.Ordinal) || asset.RelativePath.StartsWith("assets/vendor-", StringComparison.Ordinal))
    {
        await ReadAsync(asset.RelativePath);
    }
}

double firstPage = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
foreach (AssetDescriptor asset in source.Manifest.Assets) await ReadAsync(asset.RelativePath);
double all = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

string status = File.ReadAllText("/proc/self/status");
string maps = File.ReadAllText("/proc/self/maps");
string peak = status.Split('\n').First(line => line.StartsWith("VmHWM:", StringComparison.Ordinal)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"{{\"loadMs\":{loaded:F3},\"firstPageMs\":{firstPage:F3},\"allMs\":{all:F3},\"peakKiB\":{peak},\"openssl\":{(maps.Contains("libcrypto", StringComparison.Ordinal) ? "true" : "false")},\"assets\":{source.Manifest.Assets.Count}}}"));

async Task ReadAsync(string path)
{
    await using AssetReadSnapshot snapshot = await source.OpenAsync(path);
    await snapshot.Content.CopyToAsync(Stream.Null);
}
