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
//   GtkRecentManager and KDE read, written as GLib's GBookmarkFile writes it (a whole-file replace), without GTK.
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

    // xbelPath: the Linux file, for tests; null for $XDG_DATA_HOME/recently-used.xbel.
    internal RecentDocuments(string? xbelPath) => _xbelPath = xbelPath;

    public CapabilityStatus Status { get; } = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() || OperatingSystem.IsLinux()
        ? new CapabilityStatus.Available()
        : new CapabilityStatus.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);

    private static readonly PlatformResult<PlatformUnit> Done = new PlatformResult<PlatformUnit>.Success(default);

    public async ValueTask<PlatformResult<PlatformUnit>> AddAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        DesktopHost.ValidateDocumentPath(path, nameof(path));
        cancellationToken.ThrowIfCancellationRequested();
        if (_xbelPath is not null) return await Xbel.AddAsync(_xbelPath, path, cancellationToken).ConfigureAwait(false);
        if (OperatingSystem.IsMacOS())
        {
            await MacOsWkWebViewHost.NoteRecentDocumentAsync(path, cancellationToken).ConfigureAwait(false);
            return Done;
        }
        if (OperatingSystem.IsWindows()) return await Task.Run(() => Windows.Add(path), cancellationToken).ConfigureAwait(false);
        if (OperatingSystem.IsLinux()) return await Xbel.AddAsync(Xbel.DefaultPath(), path, cancellationToken).ConfigureAwait(false);
        return Unavailable;
    }

    public async ValueTask<PlatformResult<PlatformUnit>> ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_xbelPath is not null) return await Xbel.ClearAsync(_xbelPath, cancellationToken).ConfigureAwait(false);
        if (OperatingSystem.IsMacOS())
        {
            await MacOsWkWebViewHost.ClearRecentDocumentsAsync(cancellationToken).ConfigureAwait(false);
            return Done;
        }
        if (OperatingSystem.IsWindows()) return await Task.Run(Windows.Clear, cancellationToken).ConfigureAwait(false);
        if (OperatingSystem.IsLinux()) return await Xbel.ClearAsync(Xbel.DefaultPath(), cancellationToken).ConfigureAwait(false);
        return Unavailable;
    }

    private static PlatformResult<PlatformUnit> Unavailable =>
        new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);

    private static unsafe partial class Windows
    {
        private const uint ShardPathW = 0x3;
        private const uint ClsctxInprocServer = 0x1;
        private static readonly Guid ApplicationDestinationsClass = new("86c14003-4d6b-4ef3-a7b4-0506663b2e68");
        private static readonly Guid ApplicationDestinationsInterface = new("12337d35-94c6-48a0-bce7-6a9c69d4d600");

        internal static PlatformResult<PlatformUnit> Add(string path)
        {
            fixed (char* text = path) SHAddToRecentDocs(ShardPathW, text);
            return Done;
        }

        // IApplicationDestinations: IUnknown, then SetAppID, RemoveDestination, RemoveAllDestinations. The process's
        // explicit AppUserModelID, or the one Windows derives, names the Jump List.
        internal static PlatformResult<PlatformUnit> Clear()
        {
            var initialized = CoInitializeEx(0, 0x0 /* COINIT_MULTITHREADED */);
            try
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
            finally
            {
                if (initialized >= 0) CoUninitialize();
            }
        }

        private static PlatformResult<PlatformUnit> Failed(int hr) =>
            new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Other, PlatformDiagnostic.FromHResult(hr));

        [LibraryImport("shell32.dll")]
        private static partial void SHAddToRecentDocs(uint flags, void* path);

        [LibraryImport("ole32.dll")]
        private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint instance);

        [LibraryImport("ole32.dll")]
        private static partial int CoInitializeEx(nint reserved, uint model);

        [LibraryImport("ole32.dll")]
        private static partial void CoUninitialize();
    }

    // recently-used.xbel (https://www.freedesktop.org/wiki/Specifications/desktop-bookmark-spec/), as GBookmarkFile
    // writes it. Writers replace the whole file; GLib monitors it, so GTK applications see the change.
    internal static class Xbel
    {
        private static readonly XNamespace BookmarkNs = "http://www.freedesktop.org/standards/desktop-bookmarks";
        private static readonly XNamespace MimeNs = "http://www.freedesktop.org/standards/shared-mime-info";
        private static readonly SemaphoreSlim Gate = new(1, 1);

        internal static string DefaultPath()
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(data) || !Path.IsPathRooted(data))
                data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(data, "recently-used.xbel");
        }

        // The application's name and command line in the bookmark, as GLib's prgname and "'prgname %u'".
        internal static string ApplicationName =>
            Path.GetFileNameWithoutExtension(Environment.ProcessPath) is { Length: > 0 } name ? name : "runic";

        private static string ApplicationExec => "'" + (Environment.ProcessPath ?? ApplicationName) + " %u'";

        internal static async ValueTask<PlatformResult<PlatformUnit>> AddAsync(string file, string path, CancellationToken cancellationToken)
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return Update(file, root =>
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
                        .FirstOrDefault(element => (string?)element.Attribute("name") == ApplicationName);
                    if (application is null)
                    {
                        applications.Add(new XElement(BookmarkNs + "application", new XAttribute("name", ApplicationName),
                            new XAttribute("exec", ApplicationExec), new XAttribute("modified", now), new XAttribute("count", "1")));
                    }
                    else
                    {
                        application.SetAttributeValue("modified", now);
                        application.SetAttributeValue("count",
                            (int.TryParse((string?)application.Attribute("count"), out var count) ? count + 1 : 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                });
            }
            finally
            {
                Gate.Release();
            }
        }

        // Removes this application from every bookmark, and the bookmarks no other application registered.
        internal static async ValueTask<PlatformResult<PlatformUnit>> ClearAsync(string file, CancellationToken cancellationToken)
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!File.Exists(file)) return Done;
                return Update(file, root =>
                {
                    foreach (var bookmark in root.Elements("bookmark").ToList())
                    {
                        var applications = bookmark.Element("info")?.Elements("metadata").Elements(BookmarkNs + "applications").FirstOrDefault();
                        var ours = applications?.Elements(BookmarkNs + "application")
                            .Where(element => (string?)element.Attribute("name") == ApplicationName).ToList() ?? [];
                        if (ours.Count == 0) continue;
                        foreach (var element in ours) element.Remove();
                        if (!applications!.Elements(BookmarkNs + "application").Any()) bookmark.Remove();
                    }
                });
            }
            finally
            {
                Gate.Release();
            }
        }

        private static XElement Metadata(XElement bookmark)
        {
            var info = bookmark.Element("info");
            if (info is null) bookmark.Add(info = new XElement("info"));
            var metadata = info.Elements("metadata").FirstOrDefault(element => (string?)element.Attribute("owner") == "http://freedesktop.org");
            if (metadata is null) info.Add(metadata = new XElement("metadata", new XAttribute("owner", "http://freedesktop.org")));
            return metadata;
        }

        // Reads the file (a missing one is empty), applies the change and replaces the file through a temporary file in
        // the same directory, so readers never see a partial file.
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
                    catch (XmlException)
                    {
                        // GLib also starts a new list when the file is not a bookmark file.
                        document = Empty();
                    }
                    if (document.Root?.Name != "xbel") document = Empty();
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
