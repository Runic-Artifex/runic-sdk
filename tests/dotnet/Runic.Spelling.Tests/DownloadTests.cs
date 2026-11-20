using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TUnit.Core;
using static Runic.Spelling.Tests.Check;

namespace Runic.Spelling.Tests;

// Downloads from a local HTTP server, and the pins of the catalogue that ships.
public sealed class DownloadTests
{
    [Test]
    public async Task ADownloadIsVerifiedAndInstalledWithItsLicence()
    {
        using var temporary = new TemporaryFolder();
        await using var server = await DictionaryServer.StartAsync();
        var dictionaries = Dictionaries(temporary, server.Catalog("de_CH", "de-CH"));
        var entry = dictionaries.Catalog.Find("de_CH")!;
        Equal(SpellingAvailabilityStatus.Downloadable, dictionaries.GetAvailability("de-CH").Status);
        List<SpellingDownloadProgress> reported = [];

        var installed = await dictionaries.DownloadAsync(entry, new Synchronous<SpellingDownloadProgress>(reported.Add));

        var folder = Path.Combine(dictionaries.DownloadDirectory, "de_CH");
        Equal(Path.Combine(folder, "de_CH.dic"), installed.DictionaryPath);
        Equal(SpellingDictionarySource.Downloaded, installed.Source);
        Sequence(["LICENSE", "README.md", "de_CH.aff", "de_CH.dic", "source.json"],
            Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal));
        Equal(DictionaryServer.License, File.ReadAllText(Path.Combine(folder, "LICENSE")));
        True(File.ReadAllText(Path.Combine(folder, "source.json")).Contains("\"license\": \"(GPL-2.0 OR GPL-3.0)\"", StringComparison.Ordinal), "source.json names the licence");
        // Only the download folder holds anything: no folder of the download is left.
        Sequence(["de_CH"], Directory.EnumerateFileSystemEntries(dictionaries.DownloadDirectory).Select(Path.GetFileName).OfType<string>());
        Equal(new SpellingDownloadProgress(0, entry.Size), reported[0]);
        Equal(new SpellingDownloadProgress(entry.Size, entry.Size), reported[^1]);
        // The request carries a generic User-Agent and nothing else of the user's.
        True(server.UserAgents.All(agent => agent.StartsWith("Runic.Spelling/", StringComparison.Ordinal)), string.Join(", ", server.UserAgents));

        var availability = dictionaries.GetAvailability("de-CH");
        Equal(SpellingAvailabilityStatus.Downloaded, availability.Status);
        Equal(null, availability.Download);
        var checker = await SpellChecker.LoadAsync(availability.Installed!);
        Sequence(["Strase"], checker.Check("Strasse Strase").Select(found => found.Word));

        // A second download replaces the first.
        var again = await dictionaries.DownloadAsync(entry);
        Equal(installed.DictionaryPath, again.DictionaryPath);
        Sequence(["de_CH"], Directory.EnumerateFileSystemEntries(dictionaries.DownloadDirectory).Select(Path.GetFileName).OfType<string>());
    }

    [Test]
    public async Task AFileWithAnotherHashInstallsNothing()
    {
        using var temporary = new TemporaryFolder();
        await using var server = await DictionaryServer.StartAsync();
        // The server sends other bytes of the same size for the affix file.
        var dictionaries = Dictionaries(temporary, server.Catalog("de_CH", "de-CH", tamperedAffix: true));

        var error = await ThrowsAsync<InvalidDataException>(() => dictionaries.DownloadAsync(dictionaries.Catalog.Find("de_CH")!));

        True(error.Message.Contains("SHA-256", StringComparison.Ordinal), error.Message);
        Equal(0, Directory.EnumerateFileSystemEntries(dictionaries.DownloadDirectory).Count());
        Equal(SpellingAvailabilityStatus.Downloadable, dictionaries.GetAvailability("de-CH").Status);
    }

    [Test]
    public async Task ACancelledDownloadLeavesNothingHalfInstalled()
    {
        using var temporary = new TemporaryFolder();
        await using var server = await DictionaryServer.StartAsync();
        var dictionaries = Dictionaries(temporary, server.Catalog("de_CH", "de-CH", stallDictionary: true));
        using var cancellation = new CancellationTokenSource();
        var progress = new Synchronous<SpellingDownloadProgress>(report =>
        {
            // Part of the word list has arrived; the server waits for the rest.
            if (report.BytesReceived > 0) cancellation.Cancel();
        });

        await ThrowsAsync<OperationCanceledException>(() => dictionaries.DownloadAsync(dictionaries.Catalog.Find("de_CH")!, progress, cancellation.Token));

        Equal(0, Directory.EnumerateFileSystemEntries(dictionaries.DownloadDirectory).Count());
        Equal(null, dictionaries.Find("de-CH"));
    }

    [Test]
    public async Task OnlyTheCataloguesEntriesAndHttpsAddressesAreDownloaded()
    {
        using var temporary = new TemporaryFolder();
        await using var server = await DictionaryServer.StartAsync();
        var local = server.Catalog("de_CH", "de-CH");
        // An entry of another catalogue.
        await ThrowsAsync<ArgumentException>(() => Dictionaries(temporary, local).DownloadAsync(SpellingCatalog.Default.Find("de_CH")!));
        // A catalogue of http:// addresses is refused unless the tests allow their local server.
        Throws<InvalidDataException>(() => SpellingCatalog.Read(new MemoryStream(Encoding.UTF8.GetBytes(server.Json("de_CH", "de-CH")))));
        var withoutLoopback = new SpellingDictionaries(new() { Environment = Environments.Fake(SpellingPlatform.Linux, temporary.Path), Catalog = local });
        await ThrowsAsync<ArgumentException>(() => withoutLoopback.DownloadAsync(local.Find("de_CH")!));
    }

    [Test]
    public void TheShippedCatalogueIsPinnedToCommitsOverHttps()
    {
        var catalogue = SpellingCatalog.Default;
        string[] sources = ["https://github.com/wooorm/dictionaries", "https://github.com/LibreOffice/dictionaries"];

        True(catalogue.Entries.Count >= 80, $"{catalogue.Entries.Count} dictionaries");
        foreach (var entry in catalogue.Entries)
        {
            True(sources.Contains(entry.Source.ToString().TrimEnd('/')), $"{entry.Name} comes from {entry.Source}");
            True(entry.Commit.Length == 40 && entry.Commit.All(char.IsAsciiHexDigitLower), $"{entry.Name} names a commit, not {entry.Commit}");
            True(entry.Files.Any(file => file.Kind == SpellingCatalogFileKind.Dictionary) && entry.Files.Any(file => file.Kind == SpellingCatalogFileKind.Affix), entry.Name);
            True(entry.Files.Any(file => file.Kind == SpellingCatalogFileKind.Readme), $"{entry.Name} has its readme");
            True(entry.License.Length > 0, $"{entry.Name} names its licence");
            Equal(entry.Files.Sum(file => file.Size), entry.Size);
            var repository = entry.Source.AbsolutePath.Trim('/');
            foreach (var file in entry.Files)
            {
                True(file.Url.Scheme == Uri.UriSchemeHttps && file.Url.Host == "raw.githubusercontent.com", file.Url.ToString());
                True(file.Url.AbsolutePath.StartsWith($"/{repository}/{entry.Commit}/", StringComparison.Ordinal), file.Url.ToString());
                Equal(64, file.Sha256.Length);
            }
        }
        // German comes from LibreOffice's frami dictionaries, whose Swiss one is Swiss spelling, with both GPL texts.
        var swiss = catalogue.Find("de_CH")!;
        Equal("de-CH", swiss.LanguageTag);
        Equal("https://github.com/LibreOffice/dictionaries", swiss.Source.ToString());
        Equal("(GPL-2.0 OR GPL-3.0)", swiss.License);
        Equal(2, swiss.Files.Count(file => file.Kind == SpellingCatalogFileKind.License));
        Equal(null, catalogue.Find("de"));
        // English is bundled, but its other regions can be downloaded.
        Equal("https://github.com/wooorm/dictionaries", catalogue.Find("en_AU")!.Source.ToString());
    }

    // DEV-SEC-87: a downloaded dictionary counts as installed, and loads, only while its .dic and .aff are the files it
    // was installed with; otherwise it is offered for download again.
    [Test]
    public async Task AnInstalledDictionaryWithOtherFilesIsOfferedAgainAndDoesNotLoad()
    {
        using var temporary = new TemporaryFolder();
        await using var server = await DictionaryServer.StartAsync();
        var dictionaries = Dictionaries(temporary, server.Catalog("de_CH", "de-CH"));
        var entry = dictionaries.Catalog.Find("de_CH")!;
        var installed = await dictionaries.DownloadAsync(entry);
        Equal(SpellingAvailabilityStatus.Downloaded, dictionaries.GetAvailability("de-CH").Status);

        // Truncated, as a crash right after the install could leave it: the size differs.
        var original = File.ReadAllBytes(installed.DictionaryPath);
        File.WriteAllBytes(installed.DictionaryPath, original[..(original.Length / 2)]);
        var truncated = dictionaries.GetAvailability("de-CH");
        Equal(SpellingAvailabilityStatus.Downloadable, truncated.Status);
        Equal(null, truncated.Installed);
        Equal("de_CH", truncated.Download?.Name);
        // A dictionary found before the change does not load either.
        await ThrowsAsync<InvalidDataException>(() => SpellChecker.LoadAsync(installed));

        // The same size with another byte, written later: the SHA-256 differs.
        var changed = (byte[])original.Clone();
        changed[^2] ^= 0x01;
        File.WriteAllBytes(installed.DictionaryPath, changed);
        File.SetLastWriteTimeUtc(installed.DictionaryPath, DateTime.UtcNow.AddMinutes(1));
        Equal(SpellingAvailabilityStatus.Downloadable, dictionaries.GetAvailability("de-CH").Status);
        await ThrowsAsync<InvalidDataException>(() => SpellChecker.LoadAsync(installed));

        // Downloading it again mends it.
        var again = await dictionaries.DownloadAsync(entry);
        Equal(SpellingAvailabilityStatus.Downloaded, dictionaries.GetAvailability("de-CH").Status);
        Sequence(["Strase"], (await SpellChecker.LoadAsync(again)).Check("Strasse Strase").Select(found => found.Word));
    }

    // An install of another pin, such as one that an application with another release of Runic.Spelling put in the shared
    // directory, is checked against the record its download wrote (source.json); without that record it does not count.
    [Test]
    public async Task AnInstallOfAnotherPinIsCheckedAgainstItsRecord()
    {
        using var temporary = new TemporaryFolder();
        await using var server = await DictionaryServer.StartAsync();
        var dictionaries = Dictionaries(temporary, server.Catalog("de_CH", "de-CH"));
        var installed = await dictionaries.DownloadAsync(dictionaries.Catalog.Find("de_CH")!);
        var source = Path.Combine(Path.GetDirectoryName(installed.DictionaryPath)!, "source.json");
        var record = File.ReadAllText(source);
        var commit = System.Text.RegularExpressions.Regex.Match(record, "\"commit\": \"([^\"]+)\"").Groups[1].Value;
        True(commit.Length > 0, "source.json names the commit");

        File.WriteAllText(source, record.Replace(commit, "another-pin", StringComparison.Ordinal));
        Equal(SpellingAvailabilityStatus.Downloaded, dictionaries.GetAvailability("de-CH").Status);
        // Its record's sizes and hashes still count: a truncated file is not installed.
        var bytes = File.ReadAllBytes(installed.DictionaryPath);
        File.WriteAllBytes(installed.DictionaryPath, bytes[..^1]);
        Equal(SpellingAvailabilityStatus.Downloadable, dictionaries.GetAvailability("de-CH").Status);
        File.WriteAllBytes(installed.DictionaryPath, bytes);

        File.Delete(source);
        Equal(SpellingAvailabilityStatus.Downloadable, dictionaries.GetAvailability("de-CH").Status);
    }

    private static SpellingDictionaries Dictionaries(TemporaryFolder temporary, SpellingCatalog catalogue) => new(new()
    {
        Environment = Environments.Fake(SpellingPlatform.Linux, temporary.Path),
        UseSystemDictionaries = false,
        ApplicationDirectories = [],
        Catalog = catalogue,
        AllowLoopbackDownloads = true,
    });
}

// Progress reported on the reporting thread, not posted to a synchronization context.
internal sealed class Synchronous<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

// A local server of one dictionary's files, and a catalogue naming them.
internal sealed class DictionaryServer : IAsyncDisposable
{
    internal const string License = "A licence for the tests.\n";

    private static readonly byte[] Affix = Encoding.UTF8.GetBytes("SET UTF-8\nTRY esianrtolcdugmphbyfvkwzESIANRTOLCDUGMPHBYFVKWZ\n");
    // Long enough that a stalled download has sent part of it.
    private static readonly byte[] Words = Encoding.UTF8.GetBytes(string.Join('\n', ["3", "Strasse", "Haus", "Gasse", .. Enumerable.Range(0, 20_000).Select(i => $"Fuellwort{i}")]) + "\n");

    private readonly HttpListener _listener;
    private readonly Task _serving;
    private readonly CancellationTokenSource _stop = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _userAgents = new();

    private DictionaryServer(HttpListener listener, string prefix)
    {
        _listener = listener;
        Prefix = prefix;
        _serving = Task.Run(ServeAsync);
    }

    internal string Prefix { get; }

    internal IReadOnlyCollection<string> UserAgents => _userAgents;

    internal static Task<DictionaryServer> StartAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var prefix = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                return Task.FromResult(new DictionaryServer(listener, prefix));
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close();
            }
        }
    }

    internal SpellingCatalog Catalog(string name, string tag, bool tamperedAffix = false, bool stallDictionary = false) =>
        SpellingCatalog.Read(new MemoryStream(Encoding.UTF8.GetBytes(Json(name, tag, tamperedAffix, stallDictionary))), allowLoopback: true);

    internal string Json(string name, string tag, bool tamperedAffix = false, bool stallDictionary = false)
    {
        string File(string kind, string path, byte[] bytes) =>
            $$"""{ "kind": "{{kind}}", "url": "{{Prefix}}{{path}}", "sha256": "{{Convert.ToHexStringLower(SHA256.HashData(bytes))}}", "size": {{bytes.Length}} }""";
        return $$"""
            {
              "schema": "runic.spelling.catalogue/1",
              "dictionaries": [
                { "tag": "{{tag}}", "name": "{{name}}", "language": "de", "region": "CH", "license": "(GPL-2.0 OR GPL-3.0)", "size": 0,
                  "repository": "https://example.test/dictionaries", "commit": "0123456789abcdef0123456789abcdef01234567",
                  "files": [
                    {{File("dic", stallDictionary ? "stall/index.dic" : "index.dic", Words)}},
                    {{File("aff", tamperedAffix ? "tampered/index.aff" : "index.aff", Affix)}},
                    {{File("readme", "readme.md", Encoding.UTF8.GetBytes("# readme\n"))}},
                    {{File("license", "license", Encoding.UTF8.GetBytes(License))}}
                  ] }
              ]
            }
            """;
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            _ = Task.Run(() => RespondAsync(context));
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        _userAgents.Enqueue(context.Request.UserAgent ?? "");
        var path = context.Request.Url!.AbsolutePath.TrimStart('/');
        var response = context.Response;
        try
        {
            byte[]? body = path switch
            {
                "index.dic" or "stall/index.dic" => Words,
                "index.aff" => Affix,
                // Another affix file of the same size.
                "tampered/index.aff" => [.. Affix.Select(value => value == (byte)'e' ? (byte)'x' : value)],
                "readme.md" => Encoding.UTF8.GetBytes("# readme\n"),
                "license" => Encoding.UTF8.GetBytes(License),
                _ => null,
            };
            if (body is null)
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }
            response.ContentLength64 = body.Length;
            if (path.StartsWith("stall/", StringComparison.Ordinal))
            {
                await response.OutputStream.WriteAsync(body.AsMemory(0, body.Length / 2));
                await response.OutputStream.FlushAsync();
                // The rest never comes; the client cancels.
                await Task.Delay(Timeout.Infinite, _stop.Token);
            }
            await response.OutputStream.WriteAsync(body);
            response.Close();
        }
        catch (Exception)
        {
            response.Abort();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Close();
        try
        {
            await _serving;
        }
        catch (Exception)
        {
        }
        _stop.Dispose();
    }
}
