using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// B5: the application's recent documents on every desktop (IDesktopRecentDocuments). One implementation, which
// DesktopHost.NoteRecentDocumentAsync and ClearRecentDocumentsAsync and the Views hosts' service share.
// - macOS: NSDocumentController on the main thread (MacOsWkWebViewHost.Documents).
// - Windows: SHAddToRecentDocs(SHARD_PATHW), which also fills the application's Jump List Recent category where it is
//   registered for the file type. Clearing removes the application's Jump List destinations
//   (IApplicationDestinations.RemoveAllDestinations); SHAddToRecentDocs(NULL) would clear every application's.
// - Linux: the freedesktop.org desktop bookmark spec's recently-used.xbel in $XDG_DATA_HOME, which GTK 3 and 4's
//   GtkRecentManager and KDE read, written as GLib's GBookmarkFile writes it (a whole-file replace), without GTK, on
//   the thread pool. A file that does not parse is left alone and the request fails; the list keeps the newest 500.
// Paths are made absolute (Path.GetFullPath) before they are noted.
internal sealed partial class RecentDocuments : IDesktopRecentDocuments, IDesktopServiceStatus
{
    private static RecentDocuments s_shared = new(null);

    // The process's instance. Tests replace it with one over a temporary xbel file on every platform, so they never
    // change the user's recent documents.
    internal static RecentDocuments Shared
    {
        get => Volatile.Read(ref s_shared);
        set => Volatile.Write(ref s_shared, value);
    }

    private readonly string? _xbelPath;
    private readonly Xbel.Application _application;

    // xbelPath: the Linux file, for tests; null for $XDG_DATA_HOME/recently-used.xbel. applicationName: the Linux
    // application's name in the file, such as the application ID; null for the entry assembly's name.
    internal RecentDocuments(string? xbelPath, string? applicationName = null)
    {
        _xbelPath = xbelPath;
        _application = Xbel.Application.Current(applicationName);
    }

    // The Linux identity, for tests.
    internal string ApplicationName => _application.Name;

    public CapabilityStatus Status { get; } = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() || OperatingSystem.IsLinux()
        ? new CapabilityStatus.Available()
        : new CapabilityStatus.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);

    private static readonly PlatformResult<PlatformUnit> Done = new PlatformResult<PlatformUnit>.Success(default);

    public async ValueTask<PlatformResult<PlatformUnit>> AddAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        DesktopHost.ValidateDocumentPath(path, nameof(path));
        cancellationToken.ThrowIfCancellationRequested();
        // "/home/me/a/../b.txt" is the document "/home/me/b.txt".
        path = Path.GetFullPath(path);
        if (_xbelPath is not null) return await Xbel.AddAsync(_xbelPath, _application, path, cancellationToken).ConfigureAwait(false);
        if (OperatingSystem.IsMacOS())
        {
            await MacOsWkWebViewHost.NoteRecentDocumentAsync(path, cancellationToken).ConfigureAwait(false);
            return Done;
        }
        if (OperatingSystem.IsWindows()) return await Windows.AddAsync(path).ConfigureAwait(false);
        if (OperatingSystem.IsLinux()) return await Xbel.AddAsync(Xbel.DefaultPath(), _application, path, cancellationToken).ConfigureAwait(false);
        return Unavailable;
    }

    public async ValueTask<PlatformResult<PlatformUnit>> ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_xbelPath is not null) return await Xbel.ClearAsync(_xbelPath, _application, cancellationToken).ConfigureAwait(false);
        if (OperatingSystem.IsMacOS())
        {
            await MacOsWkWebViewHost.ClearRecentDocumentsAsync(cancellationToken).ConfigureAwait(false);
            return Done;
        }
        if (OperatingSystem.IsWindows()) return await Windows.ClearAsync().ConfigureAwait(false);
        if (OperatingSystem.IsLinux()) return await Xbel.ClearAsync(Xbel.DefaultPath(), _application, cancellationToken).ConfigureAwait(false);
        return Unavailable;
    }

    private static PlatformResult<PlatformUnit> Unavailable =>
        new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static unsafe partial class Windows
    {
        private const uint ShardPathW = 0x3;
        private const uint ClsctxInprocServer = 0x1;
        private static readonly Guid ApplicationDestinationsClass = new("86c14003-4d6b-4ef3-a7b4-0506663b2e68");
        private static readonly Guid ApplicationDestinationsInterface = new("12337d35-94c6-48a0-bce7-6a9c69d4d600");

        // The shell's functions expect a single-threaded apartment: each request runs on an STA thread of its own, which
        // .NET initializes COM on.
        internal static Task<PlatformResult<PlatformUnit>> RunAsync(Func<PlatformResult<PlatformUnit>> request)
        {
            var result = new TaskCompletionSource<PlatformResult<PlatformUnit>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { result.TrySetResult(request()); }
                catch (Exception error) { result.TrySetException(error); }
            }) { IsBackground = true, Name = "Runic recent documents" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return result.Task;
        }

        internal static Task<PlatformResult<PlatformUnit>> AddAsync(string path) => RunAsync(() => Add(path));

        internal static Task<PlatformResult<PlatformUnit>> ClearAsync() => RunAsync(Clear);

        internal static PlatformResult<PlatformUnit> Add(string path)
        {
            fixed (char* text = path) SHAddToRecentDocs(ShardPathW, text);
            return Done;
        }

        // IApplicationDestinations: IUnknown, then SetAppID, RemoveDestination, RemoveAllDestinations. The process's
        // explicit AppUserModelID, or the one Windows derives, names the Jump List.
        internal static PlatformResult<PlatformUnit> Clear()
        {
            var clsid = ApplicationDestinationsClass;
            var iid = ApplicationDestinationsInterface;
            var hr = CoCreateInstance(&clsid, 0, ClsctxInprocServer, &iid, out var destinations);
            if (hr < 0) return Failed(hr);
            try
            {
                var vtable = *(nint**)destinations;
                hr = ((delegate* unmanaged[Stdcall]<nint, int>)vtable[5])(destinations);
                // E_UNEXPECTED: the application has no Jump List yet, so there is nothing to clear.
                return hr >= 0 || hr == unchecked((int)0x8000FFFF) ? Done : Failed(hr);
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)destinations)[2])(destinations);
            }
        }

        private static PlatformResult<PlatformUnit> Failed(int hr) =>
            new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Other, PlatformDiagnostic.FromHResult(hr));

        [LibraryImport("shell32.dll")]
        private static partial void SHAddToRecentDocs(uint flags, void* path);

        [LibraryImport("ole32.dll")]
        private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint instance);
    }

    // recently-used.xbel (https://www.freedesktop.org/wiki/Specifications/desktop-bookmark-spec/), as GBookmarkFile
    // writes it. Writers replace the whole file; GLib monitors it, so GTK applications see the change.
    internal static class Xbel
    {
        private static readonly XNamespace BookmarkNs = "http://www.freedesktop.org/standards/desktop-bookmarks";
        private static readonly XNamespace MimeNs = "http://www.freedesktop.org/standards/shared-mime-info";
        private static readonly SemaphoreSlim Gate = new(1, 1);

        // The newest bookmarks a write keeps, as GtkRecentManager's own limit keeps the file small.
        internal const int MaximumBookmarks = 500;

        // This application in the file: its name, and its command line as GLib writes it: g_shell_quote of the
        // command, whose own arguments are shell-quoted, followed by " %u".
        internal sealed record Application(string Name, string Exec)
        {
            // applicationName, else the entry assembly's name, so `dotnet app.dll` is "app" and not "dotnet". The command
            // runs the process, and under the dotnet host also the application's assembly.
            internal static Application Current(string? applicationName)
            {
                var entry = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
                var process = Environment.ProcessPath;
                var name = applicationName is { Length: > 0 } ? applicationName
                    : entry is { Length: > 0 } ? entry
                    : Path.GetFileNameWithoutExtension(process) is { Length: > 0 } executable ? executable : "runic";
                List<string> command = [process ?? name];
                if (process is not null && entry is { Length: > 0 } && string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase))
                    command.Add(Path.Combine(AppContext.BaseDirectory, entry + ".dll"));
                return new Application(name, ShellQuote(string.Join(' ', command.Select(ShellQuote)) + " %u"));
            }
        }

        // g_shell_quote: the text in single quotes, each ' as '\''.
        internal static string ShellQuote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        internal static string DefaultPath()
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(data) || !Path.IsPathRooted(data))
                data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(data, "recently-used.xbel");
        }

        internal static async ValueTask<PlatformResult<PlatformUnit>> AddAsync(string file, Application self, string path, CancellationToken cancellationToken)
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await UpdateAsync(file, root =>
                {
                    var now = Timestamp(DateTime.UtcNow);
                    var href = FileUri(path);
                    var bookmark = root.Elements("bookmark").FirstOrDefault(element => (string?)element.Attribute("href") == href);
                    if (bookmark is null)
                    {
                        bookmark = new XElement("bookmark", new XAttribute("href", href), new XAttribute("added", now));
                        root.Add(bookmark);
                    }
                    bookmark.SetAttributeValue("modified", now);
                    bookmark.SetAttributeValue("visited", now);
                    var metadata = Metadata(bookmark);
                    if (metadata.Element(MimeNs + "mime-type") is null)
                    {
                        var type = PresentationContentTypes.TryGetContentType(path, out var contentType)
                            ? contentType.Split(';')[0].Trim()
                            : "application/octet-stream";
                        metadata.AddFirst(new XElement(MimeNs + "mime-type", new XAttribute("type", type)));
                    }
                    var applications = metadata.Element(BookmarkNs + "applications");
                    if (applications is null) metadata.Add(applications = new XElement(BookmarkNs + "applications"));
                    var application = applications.Elements(BookmarkNs + "application")
                        .FirstOrDefault(element => (string?)element.Attribute("name") == self.Name);
                    if (application is null)
                    {
                        applications.Add(new XElement(BookmarkNs + "application", new XAttribute("name", self.Name),
                            new XAttribute("exec", self.Exec), new XAttribute("modified", now), new XAttribute("count", "1")));
                    }
                    else
                    {
                        application.SetAttributeValue("modified", now);
                        application.SetAttributeValue("count",
                            (int.TryParse((string?)application.Attribute("count"), out var count) ? count + 1 : 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    Prune(root);
                }).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        // Removes this application from every bookmark, and the bookmarks no other application registered.
        internal static async ValueTask<PlatformResult<PlatformUnit>> ClearAsync(string file, Application self, CancellationToken cancellationToken)
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!File.Exists(file)) return Done;
                return await UpdateAsync(file, root =>
                {
                    foreach (var bookmark in root.Elements("bookmark").ToList())
                    {
                        var applications = bookmark.Element("info")?.Elements("metadata").Elements(BookmarkNs + "applications").FirstOrDefault();
                        var ours = applications?.Elements(BookmarkNs + "application")
                            .Where(element => (string?)element.Attribute("name") == self.Name).ToList() ?? [];
                        if (ours.Count == 0) continue;
                        foreach (var element in ours) element.Remove();
                        if (!applications!.Elements(BookmarkNs + "application").Any()) bookmark.Remove();
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        // Keeps the newest MaximumBookmarks bookmarks by their last visit or change; one without a readable date is oldest.
        private static void Prune(XElement root)
        {
            var bookmarks = root.Elements("bookmark").ToList();
            if (bookmarks.Count <= MaximumBookmarks) return;
            foreach (var old in bookmarks.OrderByDescending(LastUsed).Skip(MaximumBookmarks)) old.Remove();
        }

        private static DateTime LastUsed(XElement bookmark)
        {
            var latest = DateTime.MinValue;
            foreach (var name in (string[])["visited", "modified", "added"])
            {
                if (DateTime.TryParse((string?)bookmark.Attribute(name), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var value)
                    && value > latest)
                    latest = value;
            }
            return latest;
        }

        private static XElement Metadata(XElement bookmark)
        {
            var info = bookmark.Element("info");
            if (info is null) bookmark.Add(info = new XElement("info"));
            var metadata = info.Elements("metadata").FirstOrDefault(element => (string?)element.Attribute("owner") == "http://freedesktop.org");
            if (metadata is null) info.Add(metadata = new XElement("metadata", new XAttribute("owner", "http://freedesktop.org")));
            return metadata;
        }

        // On the thread pool: the file's I/O never runs on the caller's thread, such as a UI thread.
        private static Task<PlatformResult<PlatformUnit>> UpdateAsync(string file, Action<XElement> change) =>
            Task.Run(() => Update(file, change), CancellationToken.None);

        // Reads the file (a missing one is empty), applies the change and replaces the file through a temporary file in
        // the same directory, so readers never see a partial file. A file that is not a bookmark file is other
        // applications' history too: it stays as it is, and the request fails.
        private static PlatformResult<PlatformUnit> Update(string file, Action<XElement> change)
        {
            try
            {
                XDocument document;
                if (File.Exists(file))
                {
                    try
                    {
                        using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                        document = XDocument.Load(reader);
                    }
                    catch (XmlException error)
                    {
                        return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IOError,
                            new PlatformDiagnostic("xbel", error.LineNumber, $"{file} is not a bookmark file: {error.Message}"));
                    }
                    if (document.Root?.Name != "xbel")
                        return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IOError,
                            new PlatformDiagnostic("xbel", 0, $"{file} is not a bookmark file: its root is <{document.Root?.Name}>."));
                }
                else
                {
                    document = Empty();
                }
                change(document.Root!);
                var directory = Path.GetDirectoryName(file)!;
                Directory.CreateDirectory(directory);
                var temporary = Path.Combine(directory, "." + Path.GetFileName(file) + "." + Guid.NewGuid().ToString("N")[..8]);
                try
                {
                    using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
                        document.Save(writer);
                    File.Move(temporary, file, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                return Done;
            }
            catch (UnauthorizedAccessException error)
            {
                return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied, Diagnostic(error));
            }
            catch (IOException error)
            {
                return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IOError, Diagnostic(error));
            }
        }

        private static PlatformDiagnostic Diagnostic(Exception error) => new("errno", error.HResult, error.Message);

        private static XDocument Empty() => new(new XDeclaration("1.0", "UTF-8", null), new XElement("xbel", new XAttribute("version", "1.0"),
            new XAttribute(XNamespace.Xmlns + "bookmark", BookmarkNs.NamespaceName), new XAttribute(XNamespace.Xmlns + "mime", MimeNs.NamespaceName)));

        // ISO 8601 in UTC with microseconds, as GLib 2.66 and later write.
        internal static string Timestamp(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

        // A file: URI as g_filename_to_uri builds it: each UTF-8 byte outside the unreserved characters, the
        // sub-delimiters, ':', '@' and '/' percent-encoded.
        internal static string FileUri(string path)
        {
            var builder = new StringBuilder("file://", path.Length + 16);
            foreach (var value in Encoding.UTF8.GetBytes(path))
            {
                var c = (char)value;
                if (value < 0x80 && (char.IsAsciiLetterOrDigit(c) || "-._~!$&'()*+,;=:@/".Contains(c))) builder.Append(c);
                else builder.Append('%').Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }
    }
}
