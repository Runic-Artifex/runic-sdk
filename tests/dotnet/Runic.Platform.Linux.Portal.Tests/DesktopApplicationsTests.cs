using System.Collections.Immutable;
using System.Text;
using Runic.Platform;
using Runic.Platform.Linux.Portal;
using TUnit.Core;
using Tmds.DBus.Protocol;
using static DesktopApplicationsFixture;

// The Linux application database against fixture trees in the real file formats. Every case builds the provider from
// explicit options, so nothing reads the user's XDG directories, MIME database, defaults, icon themes or session bus.
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class DesktopApplicationsTests
{
    [Test, DisplayName("applications: key files unescape values and lists and match localized keys in the specification's order")]
    public void KeyFiles()
    {
        var file = DesktopKeyFile.Parse("# comment\n[Desktop Entry]\r\nName=Plain\nName[de]=De\nName[de_DE]=DeDe\nName[de@euro]=DeEuro\nName[de_DE@euro]=DeDeEuro\n"
            + "Name[sr@latin]=Latin\nComment = \\sa b\\nc\\td\\\\e\\$f \nMimeType=text/plain;text/x-a\\;b;;\nbroken line\n[Other]\nName=Other\n");
        Check(file.Groups.SequenceEqual(["Desktop Entry", "Other"]), "groups in order");
        string? Name(string locale) => file.LocaleString("Desktop Entry", "Name", new DesktopLocale(locale));
        Check(Name("de_DE.UTF-8@euro") == "DeDeEuro" && Name("de_DE") == "DeDe" && Name("de_AT@euro") == "DeEuro" && Name("de_AT") == "De"
            && Name("de") == "De" && Name("fr_FR") == "Plain" && Name("") == "Plain" && Name("C") == "Plain" && Name("sr_RS@latin") == "Latin",
            "lang_COUNTRY@MODIFIER, lang_COUNTRY, lang@MODIFIER, lang, then the unlocalized key");
        Check(file.String("Desktop Entry", "Comment") == " a b\nc\td\\e\\$f ", $"string escapes, found '{file.String("Desktop Entry", "Comment")}'");
        Check(file.List("Desktop Entry", "MimeType").SequenceEqual(["text/plain", "text/x-a;b"]), "lists split on unescaped semicolons");
        Check(DesktopLocale.FromCultureName("de-DE") == "de_DE" && DesktopLocale.FromCultureName("sr-Latn-RS") == "sr_RS@latin", "culture names map to POSIX locales");
    }

    [Test, DisplayName("applications: Exec values split by the quoting rules without a shell")]
    public void ExecQuoting()
    {
        Check(DesktopExec.Split("app \"a b\" 'c d' e\\ f \"q\\\"u\\`o\\$t\\\\e\" %F") is { } parts
            && parts.SequenceEqual(["app", "a b", "c d", "e f", "q\"u`o$t\\e", "%F"]), "quoted arguments");
        Check(DesktopExec.Split("app \"unterminated") is null && DesktopExec.Split("app 'open") is null, "an unclosed quote is malformed");
        Check(DesktopExec.ToUri("/tmp/a b#c%;é") == "file:///tmp/a%20b%23c%25%3B%C3%A9", "paths become escaped file URIs");
        Check(DesktopExec.ToPath("file:///tmp/a%20b") == "/tmp/a b" && DesktopExec.ToPath("file://localhost/x") == "/x"
            && DesktopExec.ToPath("file://host/x") is null && DesktopExec.ToPath("https://example.org/") is null, "file URIs become paths");
    }

    [Test, DisplayName("applications: desktop entries resolve by ID with subdirectories, shadowing, Hidden, NoDisplay, OnlyShowIn, NotShowIn, TryExec and localized names")]
    public async Task EntriesAsync()
    {
        using var fixture = new DesktopApplicationsFixture();
        fixture.Executable("present-tool");
        Entry(fixture.Share, "editor.desktop", "Editor", "Name[de]=Bearbeiter\nComment=Edits text\nIcon=accessories-text-editor\nMimeType=text/plain;text/x-c;\n");
        Entry(fixture.Share, "kde4/okular.desktop", "Okular");
        Entry(fixture.Share, "shadowed.desktop", "System copy");
        Entry(fixture.DataHome, "shadowed.desktop", "User copy");
        Entry(fixture.Share, "deleted.desktop", "Deleted");
        Entry(fixture.DataHome, "deleted.desktop", "Deleted", "Hidden=true\n");
        Entry(fixture.Share, "nodisplay.desktop", "Handler", "NoDisplay=true\n");
        Entry(fixture.Share, "onlykde.desktop", "Only KDE", "OnlyShowIn=KDE;\n");
        Entry(fixture.Share, "notgnome.desktop", "Not GNOME", "NotShowIn=GNOME;\n");
        Entry(fixture.Share, "tryexec-present.desktop", "Present", "TryExec=present-tool\n");
        Entry(fixture.Share, "tryexec-absent.desktop", "Absent", "TryExec=absent-tool\n");
        Entry(fixture.Share, "noexec.desktop", "No Exec", exec: "");
        Write(Path.Combine(fixture.Share, "applications", "link.desktop"), "[Desktop Entry]\nType=Link\nName=Link\nURL=https://example.org\n");
        Write(Path.Combine(fixture.Share, "applications", "nogroup.desktop"), "[Other]\nType=Application\nName=X\nExec=x\n[Desktop Entry]\nType=Application\nName=Y\nExec=y\n");
        Entry(fixture.LocalShare, "dbus.desktop", "Activatable", "DBusActivatable=true\n", exec: "");
        Write(Path.Combine(fixture.Share, "mime", "aliases"), "text/x-c text/x-csrc\n");

        var gnome = await Applications(fixture.Create(["GNOME"], locale: "de_DE.UTF-8"));
        string[] expected = ["dbus.desktop", "editor.desktop", "kde4-okular.desktop", "shadowed.desktop", "tryexec-present.desktop"];
        Check(gnome.Select(static app => app.Id).Order(StringComparer.Ordinal).SequenceEqual(expected), $"shown in GNOME: {string.Join(", ", gnome.Select(static app => app.Id))}");
        var editor = gnome.Single(static app => app.Id == "editor.desktop");
        Check(editor is { Name: "Bearbeiter", Description: "Edits text", IconName: "accessories-text-editor", IsHidden: false }
            && editor.ContentTypes.SequenceEqual(["text/plain", "text/x-csrc"]), "localized name, comment, icon and canonical content types");
        Check(gnome.Single(static app => app.Id == "shadowed.desktop").Name == "User copy", "XDG_DATA_HOME shadows XDG_DATA_DIRS");
        Check(gnome.Select(static app => app.Name).SequenceEqual(gnome.Select(static app => app.Name).Order(StringComparer.CurrentCultureIgnoreCase)), "sorted by name");

        var kde = await Applications(fixture.Create(["KDE"]));
        Check(kde.Any(static app => app.Id == "onlykde.desktop") && kde.Any(static app => app.Id == "notgnome.desktop"), "OnlyShowIn and NotShowIn follow XDG_CURRENT_DESKTOP");
        var none = await Applications(fixture.Create());
        Check(!none.Any(static app => app.Id == "onlykde.desktop") && none.Any(static app => app.Id == "notgnome.desktop"), "no current desktop");

        var catalog = new DesktopEntryCatalog(fixture.Options(), DesktopLocale.None);
        Check(catalog.Get("nodisplay.desktop") is { NoDisplay: true } && catalog.Get("deleted.desktop") is null && catalog.Get("tryexec-absent.desktop") is null
            && catalog.Get("link.desktop") is null && catalog.Get("nogroup.desktop") is null && catalog.Get("noexec.desktop") is null && catalog.Get("okular.desktop") is null,
            "NoDisplay entries remain usable handlers; Hidden, missing TryExec, other types, a later Desktop Entry group and a missing Exec do not");
    }

    private static async Task<ImmutableArray<DesktopApplicationInfo>> Applications(LinuxDesktopApplications applications) =>
        await applications.GetApplicationsAsync() is PlatformResult<ImmutableArray<DesktopApplicationInfo>>.Success(var list) ? list : throw new InvalidOperationException("No application list.");

    // A small MIME database in the formats update-mime-database writes.
    private static void WriteMimeDatabase(DesktopApplicationsFixture fixture)
    {
        string mime = Path.Combine(fixture.Share, "mime");
        Write(Path.Combine(mime, "globs2"), """
            # This file was automatically generated by the
            # update-mime-database command. DO NOT EDIT!
            80:text/x-heavy:*.heavy
            50:text/x-long:*.long.heavy
            60:image/x-weighted-a:*.wt
            50:image/x-weighted-b:*.wt
            50:text/x-literal:special.wt
            50:text/plain:*.txt
            50:text/x-csrc:*.c
            50:text/x-c++src:*.C:cs
            50:text/x-c++src:*.cxx
            50:application/gzip:*.gz
            50:application/x-compressed-tar:*.tar.gz
            50:image/x-amb-a:*.amb
            50:image/x-amb-b:*.amb
            50:text/x-doc-notes:*.doc
            50:application/msword:*.doc
            50:image/png:*.png
            50:text/x-old:*.old
            50:text/x-pattern:report-[0-9]?.log
            50:application/x-desktop:*.desktop
            50:text/x-future:*.future:cs,unknownflag:extra
            """ + "\n");
        Write(Path.Combine(mime, "magic"), Magic(
            (50, "image/x-priority-low", [new Rule(0, 0, Ascii("PRIO"))]),
            (80, "image/x-priority-high", [new Rule(0, 0, Ascii("PRIO"))]),
            (60, "image/x-amb-b", [new Rule(0, 0, Ascii("AMBB"))]),
            (50, "image/png", [new Rule(0, 0, [0x89, (byte)'P', (byte)'N', (byte)'G'])]),
            (50, "application/x-ole-storage", [new Rule(0, 0, [0xd0, 0xcf, 0x11, 0xe0])]),
            (50, "image/x-nested", [new Rule(0, 0, Ascii("NEST")), new Rule(1, 8, Ascii("OK")), new Rule(1, 8, Ascii("YES"))]),
            (50, "image/x-masked", [new Rule(0, 0, [0x50, 0x77], Mask: [0xf0, 0xff])]),
            (50, "image/x-ranged", [new Rule(0, 4, Ascii("RNG"), Range: 8)]),
            (50, "image/x-host16", [new Rule(0, 0, [0x12, 0x34], WordSize: 2)]),
            (50, "application/x-desktop", [new Rule(0, 0, Ascii("[Desktop Entry]"))]),
            (50, "image/x-extended", [new Rule(0, 0, Ascii("EXTD"), Garbage: "!future"), new Rule(0, 0, Ascii("EXTE"))])));
        Write(Path.Combine(mime, "aliases"), "text/x-c text/x-csrc\napplication/x-gzip application/gzip\n");
        Write(Path.Combine(mime, "subclasses"), "text/x-csrc text/plain\ntext/x-c++src text/x-csrc\napplication/msword application/x-ole-storage\n"
            + "image/svg+xml application/xml\napplication/xml text/plain\napplication/x-compressed-tar application/gzip\n");
        Write(Path.Combine(mime, "generic-icons"), "text/x-csrc:text-x-script\n");
        Write(Path.Combine(mime, "icons"), "image/png:custom-png\n");
        Write(Path.Combine(mime, "text", "x-csrc.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <mime-type xmlns="http://www.freedesktop.org/standards/shared-mime-info" type="text/x-csrc">
              <!--Created automatically by update-mime-database. DO NOT EDIT!-->
              <comment>C source code</comment>
              <comment xml:lang="de">C-Quelltext</comment>
              <comment xml:lang="pt_BR">código-fonte C</comment>
              <generic-icon name="text-x-script"/>
            </mime-type>
            """);
        // A user database that redefines text/x-old with __NOGLOBS__.
        Write(Path.Combine(fixture.DataHome, "mime", "globs2"), "0:text/x-old:__NOGLOBS__\n50:text/x-old:*.older\n");
    }

    private static async Task<string> TypeAsync(LinuxDesktopApplications applications, string path, DesktopContentDetection detection = DesktopContentDetection.NameAndContent)
    {
        var result = await applications.GetContentTypeAsync(path, detection);
        return result is PlatformResult<DesktopContentType>.Success(var type) ? type.Id : result.ToString();
    }

    [Test, DisplayName("applications: file names match globs2 by literal, weight, longest pattern, case and __NOGLOBS__ without touching the file")]
    public async Task NameMatchingAsync()
    {
        using var fixture = new DesktopApplicationsFixture();
        WriteMimeDatabase(fixture);
        var applications = fixture.Create();
        const DesktopContentDetection name = DesktopContentDetection.NameOnly;
        // None of these files exist: NameOnly never touches them.
        var expected = new Dictionary<string, string>
        {
            ["/nowhere/notes.txt"] = "text/plain", ["/nowhere/NOTES.TXT"] = "text/plain", ["/nowhere/x.wt"] = "image/x-weighted-a",
            ["/nowhere/special.wt"] = "text/x-literal", ["/nowhere/SPECIAL.WT"] = "text/x-literal", ["/nowhere/main.C"] = "text/x-c++src",
            ["/nowhere/main.c"] = "text/x-csrc", ["/nowhere/a.tar.gz"] = "application/x-compressed-tar", ["/nowhere/a.gz"] = "application/gzip",
            ["/nowhere/report-12.log"] = "text/x-pattern", ["/nowhere/report-a2.log"] = "application/octet-stream", ["/nowhere/x.heavy"] = "text/x-heavy", ["/nowhere/x.long.heavy"] = "text/x-heavy",
            ["/nowhere/x.old"] = "application/octet-stream", ["/nowhere/x.older"] = "text/x-old", ["/nowhere/unknown"] = "application/octet-stream",
            ["/nowhere/x.amb"] = "image/x-amb-a", ["/nowhere/x.future"] = "text/x-future", ["/nowhere/x.FUTURE"] = "application/octet-stream",
            ["relative.txt"] = "text/plain",
        };
        foreach (var (path, type) in expected)
            Check(await TypeAsync(applications, path, name) == type, $"{path}: expected {type}, found {await TypeAsync(applications, path, name)}");
    }

    [Test, DisplayName("applications: content detection follows the recommended checking order with magic priorities, nesting, masks, ranges and word sizes")]
    public async Task ContentDetectionAsync()
    {
        using var fixture = new DesktopApplicationsFixture();
        WriteMimeDatabase(fixture);
        var applications = fixture.Create();
        string Data(string name, byte[] content) => Write(Path.Combine(fixture.Root, "files", name), content);
        var expected = new (string Path, string Type)[]
        {
            (Data("x.amb", Ascii("AMBB rest")), "image/x-amb-b"),               // Ambiguous glob, decided by magic.
            (Data("y.amb", Ascii("zzzz")), "image/x-amb-a"),                    // No magic: the first glob.
            (Data("x.doc", [0xd0, 0xcf, 0x11, 0xe0, 0, 0]), "application/msword"), // The glob candidate that is a subclass of the magic result.
            (Data("y.doc", Ascii("plain notes")), "text/x-doc-notes"),
            (Data("image", [0x89, (byte)'P', (byte)'N', (byte)'G', 0, 0]), "image/png"),
            (Data("text", Ascii("hello\nworld\t\u00e9")), "text/plain"),
            (Data("binary", [1, 2, 3, 0, 4]), "application/octet-stream"),
            (Data("empty", []), "text/plain"),
            (Data("prio", Ascii("PRIO")), "image/x-priority-high"),
            (Data("nested-ok", Ascii("NEST....YES")), "image/x-nested"),
            (Data("nested-no", Ascii("NEST....NO!")), "text/plain"),
            (Data("masked", [0x5a, 0x77, 0x41]), "image/x-masked"),
            (Data("ranged", Ascii("0123456RNG")), "image/x-ranged"),
            (Data("too-far", Ascii("012345678901RNG")), "text/plain"),
            (Data("launcher", Ascii("[Desktop Entry]\nExec=x\n")), "text/plain"), // Content alone never makes a desktop entry.
            (Data("extended-d", Ascii("EXTD")), "text/plain"),                      // A line with an unknown extension is ignored...
            (Data("extended-e", Ascii("EXTE")), "image/x-extended"),               // ...the next one is read.
            (Data("main.txt", Ascii("PRIO")), "text/plain"),                        // An unambiguous glob is not sniffed.
        };
        foreach (var (path, type) in expected)
            Check(await TypeAsync(applications, path) == type, $"{Path.GetFileName(path)}: expected {type}, found {await TypeAsync(applications, path)}");
        if (BitConverter.IsLittleEndian)
            Check(await TypeAsync(applications, Data("host16", [0x34, 0x12])) == "image/x-host16", "a host-order value is swapped on little-endian machines");

        string directory = Directory.CreateDirectory(Path.Combine(fixture.Root, "files", "folder.txt")).FullName;
        Check(await TypeAsync(applications, directory) == "inode/directory", "directories are inode/directory whatever their name");
        string dangling = Path.Combine(fixture.Root, "files", "dangling.txt");
        File.CreateSymbolicLink(dangling, Path.Combine(fixture.Root, "missing"));
        Check(await TypeAsync(applications, dangling) == "inode/symlink", "a dangling link is inode/symlink");
        string link = Path.Combine(fixture.Root, "files", "link");
        File.CreateSymbolicLink(link, Path.Combine(fixture.Root, "files", "image"));
        Check(await TypeAsync(applications, link) == "image/png", "a link is classified by its target");
        Check(await applications.GetContentTypeAsync(Path.Combine(fixture.Root, "missing")) is PlatformResult<DesktopContentType>.Failed { Code: PlatformFailureCode.IOError, Diagnostic: { Domain: "errno", Code: 2 } },
            "a missing file is an I/O error with ENOENT");

        if (Environment.UserName != "root")
        {
            string secret = Data("secret.amb", Ascii("AMBB"));
            File.SetUnixFileMode(secret, UnixFileMode.None);
            Check(await applications.GetContentTypeAsync(secret) is PlatformResult<DesktopContentType>.Failed { Code: PlatformFailureCode.PermissionDenied },
                "an unreadable file whose name is ambiguous is PermissionDenied");
            Check(await TypeAsync(applications, secret, DesktopContentDetection.NameOnly) == "image/x-amb-a", "NameOnly does not read it");
            string unread = Data("unread.txt", Ascii("x"));
            File.SetUnixFileMode(unread, UnixFileMode.None);
            Check(await TypeAsync(applications, unread) == "text/plain", "an unambiguous name needs no read");
        }
    }

    [Test, DisplayName("applications: content types carry aliases resolved, parents, extensions, icons and localized descriptions")]
    public async Task ContentTypeDetailsAsync()
    {
        using var fixture = new DesktopApplicationsFixture();
        WriteMimeDatabase(fixture);
        var german = fixture.Create(locale: "de_DE.UTF-8");
        Check(await german.GetContentTypeAsync("/nowhere/x.cxx", DesktopContentDetection.NameOnly) is PlatformResult<DesktopContentType>.Success(var cxx)
            && cxx is { Id: "text/x-c++src", MimeType: "text/x-c++src", IconName: "text-x-c++src" }
            && cxx.Parents.SequenceEqual(["text/x-csrc", "text/plain", "application/octet-stream"]) && cxx.Extensions.SequenceEqual(["C", "cxx"]),
            "parents nearest first with the implicit ones, and extensions");
        Check(await german.GetContentTypeAsync("/nowhere/x.c", DesktopContentDetection.NameOnly) is PlatformResult<DesktopContentType>.Success(var c)
            && c.Description == "C-Quelltext" && c.Extensions.SequenceEqual(["c"]), "the localized comment from text/x-csrc.xml");
        Check(await fixture.Create(locale: "pt_BR").GetContentTypeAsync("/nowhere/x.c", DesktopContentDetection.NameOnly) is PlatformResult<DesktopContentType>.Success { Value.Description: "código-fonte C" }
            && await fixture.Create().GetContentTypeAsync("/nowhere/x.c", DesktopContentDetection.NameOnly) is PlatformResult<DesktopContentType>.Success { Value.Description: "C source code" },
            "other and no locales");
        Check(await german.GetContentTypeAsync("/nowhere/x.png", DesktopContentDetection.NameOnly) is PlatformResult<DesktopContentType>.Success { Value.IconName: "custom-png" },
            "the icons file names the icon");
        var mime = SharedMimeDatabase.Load(fixture.Options().DataSearch);
        Check(mime.Unalias("text/x-c") == "text/x-csrc" && mime.Parents("inode/directory").IsEmpty && mime.Parents("x-scheme-handler/https").IsEmpty
            && mime.Parents("text/x-unknown").SequenceEqual(["text/plain", "application/octet-stream"]) && mime.Parents("application/octet-stream").IsEmpty
            && mime.Parents("application/x-compressed-tar").SequenceEqual(["application/gzip", "application/octet-stream"]),
            "inode and x-scheme-handler types are not streams; text types are text/plain");
        Check(mime.GenericIconName("text/x-csrc") == "text-x-script" && mime.GenericIconName("video/ogg") == "video-x-generic", "generic icons");
    }

    // Entries and mimeapps.list files across every level the MIME Apps specification reads.
    private static void WriteAssociations(DesktopApplicationsFixture fixture)
    {
        Write(Path.Combine(fixture.Share, "mime", "aliases"), "text/x-c text/x-csrc\n");
        Write(Path.Combine(fixture.Share, "mime", "subclasses"), "text/x-csrc text/plain\n");
        Entry(fixture.Share, "editor.desktop", "Editor", "MimeType=text/plain;text/x-csrc;\n");
        Entry(fixture.Share, "cedit.desktop", "C Editor", "MimeType=text/x-csrc;\n");
        Entry(fixture.Share, "aliased.desktop", "Aliased", "MimeType=text/x-c;\n");
        Entry(fixture.Share, "viewer.desktop", "Viewer", "MimeType=image/png;image/x-pair;\n");
        Entry(fixture.Share, "kde4/okular.desktop", "Okular", "MimeType=image/png;image/x-pair;\n");
        Entry(fixture.Share, "hexed.desktop", "Hex", "MimeType=application/octet-stream;\n");
        Entry(fixture.Share, "removed.desktop", "Removed", "MimeType=text/plain;\n");
        Entry(fixture.Share, "handler.desktop", "Handler", "NoDisplay=true\nMimeType=text/plain;\n");
        Entry(fixture.Share, "shadowed.desktop", "System", "MimeType=text/plain;\n");
        Entry(fixture.DataHome, "shadowed.desktop", "User");
        Entry(fixture.LocalShare, "notes.desktop", "Notes", "MimeType=text/plain;\n");
        Write(Path.Combine(fixture.Share, "applications", "mimeinfo.cache"), "[MIME Cache]\ntext/plain=removed.desktop;handler.desktop;editor.desktop;\n");
        Write(Path.Combine(fixture.Share, "applications", "mimeapps.list"),
            "[Default Applications]\nimage/png=viewer.desktop\n[Added Associations]\ntext/plain=shadowed.desktop;\n");
        Write(Path.Combine(fixture.ConfigDirectory, "mimeapps.list"), "[Default Applications]\ntext/plain=notes.desktop;\n");
        Write(Path.Combine(fixture.ConfigHome, "mimeapps.list"), "[Default Applications]\ntext/plain=missing.desktop;editor.desktop;\nimage/x-other=editor.desktop\nimage/x-pair=viewer.desktop;\n"
            + "[Added Associations]\nimage/png=editor.desktop;\n[Removed Associations]\ntext/plain=removed.desktop;\n");
        Write(Path.Combine(fixture.ConfigHome, "gnome-mimeapps.list"), "[Default Applications]\nimage/png=kde4-okular.desktop;\nimage/x-pair=kde4-okular.desktop;\n");
    }

    private static async Task<string[]> HandlersAsync(LinuxDesktopApplications applications, string type) =>
        await applications.GetApplicationsForAsync(type) is PlatformResult<ImmutableArray<DesktopApplicationInfo>>.Success(var list)
            ? [.. list.Select(static app => app.Id)] : throw new InvalidOperationException("No handlers.");

    private static async Task<string?> DefaultAsync(LinuxDesktopApplications applications, string type) =>
        await applications.GetDefaultApplicationAsync(type) is PlatformResult<DesktopApplicationInfo?>.Success(var info) ? info?.Id : throw new InvalidOperationException("No default.");

    [Test, DisplayName("applications: defaults and associations follow mimeapps.list levels, desktop-specific files, added and removed associations, mimeinfo.cache and inheritance")]
    public async Task AssociationsAsync()
    {
        using var fixture = new DesktopApplicationsFixture();
        WriteAssociations(fixture);
        var plain = fixture.Create();
        var gnome = fixture.Create(["GNOME"]);
        Check(await DefaultAsync(plain, "text/plain") == "editor.desktop", "the user's list, skipping a missing ID, before the system's");
        Check(await DefaultAsync(gnome, "image/png") == "kde4-okular.desktop", "a desktop-specific list comes first");
        Check(await DefaultAsync(plain, "image/png") == "viewer.desktop", "the distribution's default without the desktop-specific list");
        Check(await DefaultAsync(gnome, "image/x-pair") == "kde4-okular.desktop" && await DefaultAsync(plain, "image/x-pair") == "viewer.desktop",
            "in one directory the desktop-specific list comes before mimeapps.list");
        Check(await DefaultAsync(plain, "image/x-other") is null, "a default the application is not associated with is skipped");
        Check(await DefaultAsync(plain, "text/x-csrc") == "aliased.desktop" && await DefaultAsync(plain, "text/x-c") == "aliased.desktop",
            "a more specific type's own application beats the parent type's default; aliases resolve");
        Check((await HandlersAsync(plain, "text/plain")).SequenceEqual(["editor.desktop", "notes.desktop", "handler.desktop", "hexed.desktop"]),
            $"default first, then by level and mimeinfo.cache order, minus removed and shadowed entries, then the octet-stream parent: {string.Join(", ", await HandlersAsync(plain, "text/plain"))}");
        Check((await HandlersAsync(plain, "text/x-csrc")).SequenceEqual(["aliased.desktop", "cedit.desktop", "editor.desktop", "notes.desktop", "handler.desktop", "hexed.desktop"]),
            $"inherited through text/plain: {string.Join(", ", await HandlersAsync(plain, "text/x-csrc"))}");
        Check((await HandlersAsync(plain, "image/png")).SequenceEqual(["viewer.desktop", "editor.desktop", "kde4-okular.desktop", "hexed.desktop"]),
            $"added associations before the directory's entries: {string.Join(", ", await HandlersAsync(plain, "image/png"))}");
        Check(await plain.GetApplicationsForAsync("text/plain") is PlatformResult<ImmutableArray<DesktopApplicationInfo>>.Success(var list)
            && list.Single(static app => app.Id == "handler.desktop").IsHidden, "a NoDisplay handler is listed as hidden");
        Check(await plain.GetApplicationsForAsync("not a type") is PlatformResult<ImmutableArray<DesktopApplicationInfo>>.Failed { Code: PlatformFailureCode.InvalidData }
            && await plain.GetDefaultApplicationAsync("text/") is PlatformResult<DesktopApplicationInfo?>.Failed { Code: PlatformFailureCode.InvalidData },
            "malformed MIME types are invalid data");
        Check(await DefaultAsync(plain, "x-scheme-handler/unknown") is null && (await HandlersAsync(plain, "x-scheme-handler/unknown")).Length == 0,
            "URI schemes inherit nothing from application/octet-stream");
    }

    [Test, DisplayName("applications: SetDefault changes one key of the user's mimeapps.list, preserving everything else, and rejects unknown or unassociated applications")]
    public async Task SetDefaultAsync()
    {
        using var fixture = new DesktopApplicationsFixture();
        WriteAssociations(fixture);
        string path = Path.Combine(fixture.ConfigHome, "mimeapps.list");
        string original = "# My associations\r\n[Added Associations]\r\nimage/png=editor.desktop;\r\n\r\n[Default Applications]\r\ntext/plain=notes.desktop;\r\n"
            + "image/png=viewer.desktop\r\n# keep me\r\n\r\n[Removed Associations]\r\ntext/plain=removed.desktop;\r\n[X-Custom]\r\nkey = value \\s\r\n";
        File.WriteAllText(path, original);
        var applications = fixture.Create();
        Check(await applications.SetDefaultApplicationAsync("text/plain", "editor.desktop") is PlatformResult<PlatformUnit>.Success, "replaced");
        Check(File.ReadAllText(path) == original.Replace("text/plain=notes.desktop;", "text/plain=editor.desktop;"),
            $"only the key changed, line endings included:\n{File.ReadAllText(path)}");
        Check(await applications.SetDefaultApplicationAsync("text/x-c", "cedit.desktop") is PlatformResult<PlatformUnit>.Success, "added");
        Check(File.ReadAllText(path) == original.Replace("text/plain=notes.desktop;", "text/plain=editor.desktop;")
            .Replace("image/png=viewer.desktop\r\n", "image/png=viewer.desktop\r\ntext/x-csrc=cedit.desktop;\r\n"),
            $"a new key follows the group's last key, under the canonical type:\n{File.ReadAllText(path)}");
        Check(await DefaultAsync(applications, "text/plain") == "editor.desktop" && await DefaultAsync(applications, "text/x-csrc") == "cedit.desktop", "read back");

        string before = File.ReadAllText(path);
        var diagnostics = new List<PortalDiagnostic>();
        var observed = fixture.Create(diagnostics: diagnostics);
        Check(await observed.SetDefaultApplicationAsync("text/plain", "missing.desktop") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData }
            && await observed.SetDefaultApplicationAsync("text/plain", "/usr/share/applications/editor.desktop") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData }
            && await observed.SetDefaultApplicationAsync("text/plain", "removed.desktop") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData }
            && await observed.SetDefaultApplicationAsync("text/plain", "viewer.desktop") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData }
            && await observed.SetDefaultApplicationAsync("plain", "editor.desktop") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData },
            "unknown, path, removed and unassociated applications and malformed types are invalid data");
        Check(File.ReadAllText(path) == before && diagnostics.Any(static d => d.Code == "desktop-application-unknown") && diagnostics.Any(static d => d.Code == "desktop-application-not-associated"),
            "nothing was written, and the reasons were diagnosed");
        Check(Directory.GetFiles(fixture.ConfigHome).All(static file => !Path.GetFileName(file).Contains("runic-")), "no temporary file is left");

        // A missing file is created with just the default; a symbolic link keeps pointing at its target.
        File.Delete(path);
        var created = await applications.SetDefaultApplicationAsync("image/png", "viewer.desktop");
        Check(created is PlatformResult<PlatformUnit>.Success, $"created: {created}");
        Check(File.ReadAllText(path) == "[Default Applications]\nimage/png=viewer.desktop;\n", $"created:\n{File.ReadAllText(path)}");
        string target = Write(Path.Combine(fixture.Root, "dotfiles", "mimeapps.list"), "[Added Associations]\nimage/png=editor.desktop;");
        File.Delete(path);
        File.CreateSymbolicLink(path, target);
        var linked = await applications.SetDefaultApplicationAsync("image/png", "editor.desktop");
        Check(linked is PlatformResult<PlatformUnit>.Success, $"through the link: {linked}");
        Check(new FileInfo(path).LinkTarget == target
            && File.ReadAllText(target) == "[Added Associations]\nimage/png=editor.desktop;\n\n[Default Applications]\nimage/png=editor.desktop;\n",
            $"the link's target was updated:\n{File.ReadAllText(target)}");
        // A dangling link, to a file the dotfile manager has not created yet, gets its target written.
        string pending = Path.Combine(fixture.Root, "dotfiles", "pending", "mimeapps.list");
        Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
        File.Delete(path);
        File.CreateSymbolicLink(path, pending);
        var dangling = await applications.SetDefaultApplicationAsync("image/png", "viewer.desktop");
        Check(dangling is PlatformResult<PlatformUnit>.Success && new FileInfo(path).LinkTarget == pending
            && File.ReadAllText(pending) == "[Default Applications]\nimage/png=viewer.desktop;\n", $"through a dangling link: {dangling}");
        // A key that names the type by an alias or in other letter case is the same key; left alone, it would still win.
        File.Delete(path);
        File.WriteAllText(path, "[Default Applications]\nText/X-C=aliased.desktop;\n# end\n");
        Check(await applications.SetDefaultApplicationAsync("text/x-csrc", "cedit.desktop") is PlatformResult<PlatformUnit>.Success
            && File.ReadAllText(path) == "[Default Applications]\ntext/x-csrc=cedit.desktop;\n# end\n" && await DefaultAsync(applications, "text/x-c") == "cedit.desktop",
            $"an alias key was replaced:\n{File.ReadAllText(path)}");
        Check(MimeAppsWriter.Update("[Default Applications]\na/b=x.desktop;\n[Default Applications]\na/b=y.desktop\n", "a/b", "z.desktop")
            == "[Default Applications]\na/b=z.desktop;\n[Default Applications]\na/b=z.desktop;\n", "every occurrence of the key changes, so a later duplicate cannot win");
    }

    [Test, DisplayName("applications: inside Flatpak or Snap the host's applications are unavailable, while content types still work")]
    public async Task SandboxedAsync()
    {
        using var fixture = new DesktopApplicationsFixture();
        WriteAssociations(fixture);
        WriteMimeDatabase(fixture);
        var diagnostics = new List<PortalDiagnostic>();
        var applications = fixture.Create(sandboxed: true, diagnostics: diagnostics);
        const PlatformUnavailableReason reason = PlatformUnavailableReason.BackendUnavailable;
        Check(await applications.GetApplicationsAsync() is PlatformResult<ImmutableArray<DesktopApplicationInfo>>.Unavailable(reason)
            && await applications.GetApplicationsForAsync("text/plain") is PlatformResult<ImmutableArray<DesktopApplicationInfo>>.Unavailable(reason)
            && await applications.GetDefaultApplicationAsync("text/plain") is PlatformResult<DesktopApplicationInfo?>.Unavailable(reason)
            && await applications.SetDefaultApplicationAsync("text/plain", "editor.desktop") is PlatformResult<PlatformUnit>.Unavailable(reason)
            && await applications.LaunchAsync("editor.desktop", []) is PlatformResult<PlatformUnit>.Unavailable(reason),
            "application methods are unavailable");
        Check(diagnostics.Any(static d => d.Code == "desktop-applications-sandboxed"), "and diagnosed");
        Check(await TypeAsync(applications, "/nowhere/x.c", DesktopContentDetection.NameOnly) == "text/x-csrc", "content types come from the sandbox's own data");
    }

    [Test, DisplayName("applications: arguments are checked before any work, and cancellation is honoured")]
    public async Task ArgumentsAsync()
    {
        using var fixture = new DesktopApplicationsFixture();
        var applications = fixture.Create();
        async Task Throws<T>(Func<Task> action, string what) where T : Exception
        {
            try { await action(); }
            catch (T) { return; }
            throw new InvalidOperationException($"{what} was accepted.");
        }
        await Throws<ArgumentException>(() => applications.GetContentTypeAsync("").AsTask(), "an empty path");
        await Throws<ArgumentException>(() => applications.GetApplicationsForAsync("a\0b").AsTask(), "NUL");
        await Throws<ArgumentException>(() => applications.LaunchAsync("editor.desktop", ["relative"]).AsTask(), "a relative path");
        await Throws<ArgumentException>(() => applications.LaunchAsync("editor.desktop", default).AsTask(), "a default array");
        await Throws<ArgumentException>(() => applications.LaunchAsync("editor.desktop", [.. Enumerable.Repeat("/x", 1025)]).AsTask(), "too many items");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Throws<OperationCanceledException>(() => applications.GetApplicationsAsync(cancelled.Token).AsTask(), "a cancelled request");
        await Throws<OperationCanceledException>(() => applications.LaunchAsync("editor.desktop", [], cancelled.Token).AsTask(), "a cancelled launch");
    }

    [Test, DisplayName("applications: Launch starts only installed entries, without a shell, expanding every field code into whole arguments")]
    public async Task LaunchAsync()
    {
        using var fixture = new DesktopApplicationsFixture();
        string output = Directory.CreateDirectory(Path.Combine(fixture.Root, "probe")).FullName;
        string probe = LaunchProbe.Exec(output);
        string work = Directory.CreateDirectory(Path.Combine(fixture.Root, "work dir")).FullName;
        string files = Directory.CreateDirectory(Path.Combine(fixture.Root, "my files")).FullName;
        string spaced = Path.Combine(files, "a b.txt"), quoted = Path.Combine(files, "it's \"q\";$x`y`.txt");
        string many = Entry(fixture.Share, "many.desktop", "Many", $"Name[de]=Viele\nIcon=many-icon\nPath={work}\n",
            exec: probe + " --many \"quoted arg\" literal%%percent %F --name=%c %k %i %d %x \"%U\"");
        Entry(fixture.Share, "single.desktop", "Single", exec: probe + " --single %f");
        Entry(fixture.Share, "uris.desktop", "URIs", exec: probe + " --uris %U");
        Entry(fixture.Share, "uri.desktop", "URI", exec: probe + " --uri %u");
        Entry(fixture.Share, "nocode.desktop", "No code", exec: probe + " --nocode");
        Entry(fixture.Share, "noicon.desktop", "No icon", exec: probe + " --noicon %i");
        Entry(fixture.Share, "terminal.desktop", "Terminal", "Terminal=true\n", exec: probe + " --terminal");
        Entry(fixture.Share, "broken.desktop", "Broken", exec: probe + " \"unterminated");
        Entry(fixture.Share, "missing-program.desktop", "Missing", exec: "runic-no-such-program %f");
        Entry(fixture.Share, "bad-path.desktop", "Bad Path", $"Path={Path.Combine(fixture.Root, "no such dir")}\n", exec: probe + " --bad");
        var diagnostics = new List<PortalDiagnostic>();
        var applications = fixture.Create(locale: "de", diagnostics: diagnostics);

        Check(await applications.LaunchAsync("many.desktop", [spaced, "file://" + DesktopExec.ToUri(quoted)["file://".Length..]]) is PlatformResult<PlatformUnit>.Success, "many");
        var runs = await LaunchProbe.WaitAsync(output, 1);
        Check(runs.Count == 1 && runs[0].Directory == work, $"one process in Path=: {runs.Count} {runs.FirstOrDefault().Directory}");
        // The specification leaves a quoted field code undefined; it expands as an unquoted one does.
        string[] expected = ["--many", "quoted arg", "literal%percent", spaced, quoted, "--name=Viele", many, "--icon", "many-icon", DesktopExec.ToUri(spaced), DesktopExec.ToUri(quoted)];
        Check(runs[0].Arguments.SequenceEqual(expected), $"arguments:\n{string.Join("\n", runs[0].Arguments)}");
        Clear(output);

        Check(await applications.LaunchAsync("single.desktop", [spaced, quoted, "file:///tmp/x%20y"]) is PlatformResult<PlatformUnit>.Success, "single");
        runs = await LaunchProbe.WaitAsync(output, 3);
        Check(runs.Count == 3 && runs.Select(static run => string.Join('|', run.Arguments)).Order(StringComparer.Ordinal)
            .SequenceEqual(new[] { "--single|" + spaced, "--single|" + quoted, "--single|/tmp/x y" }.Order(StringComparer.Ordinal)),
            $"%f starts one process per file, file URIs as paths: {string.Join("; ", runs.Select(static run => string.Join('|', run.Arguments)))}");
        Clear(output);

        Check(await applications.LaunchAsync("uris.desktop", [spaced, "https://example.org/a?b=c#d", "mailto:a@example.org"]) is PlatformResult<PlatformUnit>.Success, "uris");
        runs = await LaunchProbe.WaitAsync(output, 1);
        Check(runs.Count == 1 && runs[0].Arguments.SequenceEqual(["--uris", DesktopExec.ToUri(spaced), "https://example.org/a?b=c#d", "mailto:a@example.org"]),
            $"%U passes URIs unchanged and paths as file URIs: {string.Join("|", runs.FirstOrDefault().Arguments)}");
        Clear(output);

        Check(await applications.LaunchAsync("uri.desktop", ["https://a.example/", "https://b.example/"]) is PlatformResult<PlatformUnit>.Success, "uri");
        Check((await LaunchProbe.WaitAsync(output, 2)).Count == 2, "%u starts one process per URI");
        Clear(output);

        Check(await applications.LaunchAsync("single.desktop", []) is PlatformResult<PlatformUnit>.Success, "no items");
        runs = await LaunchProbe.WaitAsync(output, 1);
        Check(runs.Count == 1 && runs[0].Arguments.SequenceEqual(["--single"]), "a file code without items disappears");
        Clear(output);

        Check(await applications.LaunchAsync("nocode.desktop", [spaced]) is PlatformResult<PlatformUnit>.Success
            && (await LaunchProbe.WaitAsync(output, 1)) is [{ Arguments: ["--nocode", var appended] }] && appended == spaced, "without a file code the item is appended");
        Clear(output);
        Check(await applications.LaunchAsync("noicon.desktop", []) is PlatformResult<PlatformUnit>.Success
            && (await LaunchProbe.WaitAsync(output, 1)) is [{ Arguments: ["--noicon"] }], "%i without an Icon key disappears");
        Clear(output);

        Check(await applications.LaunchAsync("single.desktop", [spaced, "https://example.org/"]) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData },
            "a web URI for an application that opens only local files is invalid data");
        Check(await applications.LaunchAsync("terminal.desktop", []) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable)
            && diagnostics.Any(static d => d.Code == "desktop-application-terminal"), "terminal applications are unavailable");
        Check(await applications.LaunchAsync("broken.desktop", []) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData }, "malformed Exec");
        Check(await applications.LaunchAsync("missing-program.desktop", []) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.IOError, Diagnostic.Code: 2 }, "missing program");
        Check(await applications.LaunchAsync("bad-path.desktop", []) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.IOError }, "missing working directory");
        foreach (var id in (string[])["unknown.desktop", many, "many", "../share/applications/many.desktop", Environment.ProcessPath!])
            Check(await applications.LaunchAsync(id, []) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData }, $"'{id}' is not an installed desktop ID");
        Check((await LaunchProbe.WaitAsync(output, 0)).Count == 0, "no rejected launch started a process");
    }

    [Test, DisplayName("applications: a D-Bus-activatable entry starts from its Exec key when no session bus is reachable")]
    public async Task ActivationFallbackAsync()
    {
        using var fixture = new DesktopApplicationsFixture();
        string output = Directory.CreateDirectory(Path.Combine(fixture.Root, "probe")).FullName;
        Entry(fixture.Share, "org.runic.Fallback.desktop", "Fallback", "DBusActivatable=true\n", exec: LaunchProbe.Exec(output) + " --fallback %U");
        Entry(fixture.Share, "org.runic.BusOnly.desktop", "Bus only", "DBusActivatable=true\n", exec: "");
        // The fixture's bus address names a socket that does not exist.
        var applications = fixture.Create();
        Check(await applications.LaunchAsync("org.runic.Fallback.desktop", ["/tmp/a b"]) is PlatformResult<PlatformUnit>.Success
            && (await LaunchProbe.WaitAsync(output, 1)) is [{ Arguments: ["--fallback", "file:///tmp/a%20b"] }], "the Exec key started the application");
        Check(await applications.LaunchAsync("org.runic.BusOnly.desktop", []) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "without Exec and without a bus the launch is unavailable");
    }

    // A fake application on the private bus of dbus-run-session, never the user's session bus (CI runs this category so).
    [Test, Explicit, Category("DBus"), DisplayName("applications: a D-Bus-activatable entry is opened through org.freedesktop.Application, not its Exec key")]
    public async Task DBusActivationAsync()
    {
        using var fixture = new DesktopApplicationsFixture();
        string address = DBusAddress.Session ?? throw new InvalidOperationException("Run this case under dbus-run-session.");
        using var bus = new DBusConnection(address);
        await bus.ConnectAsync();
        var application = new FakeApplication("/org/runic/Test_App");
        bus.AddMethodHandler(application);
        if (!await bus.TryRequestNameAsync("org.runic.Test-App")) throw new InvalidOperationException("Cannot own the fake application's name.");

        string output = Directory.CreateDirectory(Path.Combine(fixture.Root, "probe")).FullName;
        Entry(fixture.Share, "org.runic.Test-App.desktop", "Test", "DBusActivatable=true\n", exec: LaunchProbe.Exec(output) + " --exec %U");
        Entry(fixture.Share, "org.runic.Absent.desktop", "Absent", "DBusActivatable=true\n", exec: "");
        Entry(fixture.Share, "not-a-bus-name.desktop", "Invalid", "DBusActivatable=true\n", exec: "");
        var diagnostics = new List<PortalDiagnostic>();
        var applications = new LinuxDesktopApplications(fixture.Options() with { BusAddress = address }, new PortalApplication(null, diagnostics.Add));

        Check(await applications.LaunchAsync("org.runic.Test-App.desktop", ["/tmp/a b.txt", "https://example.org/?q=1"]) is PlatformResult<PlatformUnit>.Success,
            "Open succeeded");
        Check(application.Calls is [("Open", ["file:///tmp/a%20b.txt", "https://example.org/?q=1"])], $"Open received each item as a URI: {application}");
        Check(await applications.LaunchAsync("org.runic.Test-App.desktop", []) is PlatformResult<PlatformUnit>.Success
            && application.Calls.Count == 2 && application.Calls[1] is ("Activate", []), $"Activate without items: {application}");
        application.Fail = true;
        Check(await applications.LaunchAsync("org.runic.Test-App.desktop", []) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.Other, Diagnostic.Domain: "org.runic.Error.Failed" }
            && diagnostics.Any(static d => d.Code == "desktop-application-activation-failed"), "a D-Bus error is an I/O failure that names it");
        Check(await applications.LaunchAsync("org.runic.Absent.desktop", []) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.Other, Diagnostic.Domain: "org.freedesktop.DBus.Error.ServiceUnknown" },
            "an application that is not on the bus");
        Check(await applications.LaunchAsync("not-a-bus-name.desktop", []) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData },
            "an ID that is not a bus name");
        Check((await LaunchProbe.WaitAsync(output, 0)).Count == 0, "the Exec key never ran while the bus answered");
        Console.WriteLine("PASS applications: D-Bus activation through org.freedesktop.Application.");
    }

    private sealed class FakeApplication(string path) : IPathMethodHandler
    {
        public string Path => path;
        public bool HandlesChildPaths => false;
        internal readonly List<(string Method, string[] Uris)> Calls = [];
        internal volatile bool Fail;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            if (request.InterfaceAsString != "org.freedesktop.Application") { context.ReplyError("org.freedesktop.DBus.Error.UnknownInterface", "No."); return ValueTask.CompletedTask; }
            if (Fail) { context.ReplyError("org.runic.Error.Failed", "Refused."); return ValueTask.CompletedTask; }
            var reader = request.GetBodyReader();
            string method = request.MemberAsString ?? "";
            string[] uris = method == "Open" ? reader.ReadArrayOfString() : [];
            var platformData = reader.ReadDictionaryOfStringToVariantValue();
            lock (Calls) Calls.Add((method, uris));
            using var reply = context.CreateReplyWriter(null);
            context.Reply(reply.CreateMessage());
            return ValueTask.CompletedTask;
        }

        public override string ToString() => string.Join("; ", Calls.Select(static call => call.Method + "(" + string.Join(", ", call.Uris) + ")"));
    }

    private static void Clear(string directory)
    {
        foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
    }

    // An icon theme with every directory type, a scaled directory and inheritance, beside hicolor and pixmaps.
    private static void WriteIcons(DesktopApplicationsFixture fixture)
    {
        string icons = Path.Combine(fixture.Share, "icons");
        Write(Path.Combine(icons, "Test", "index.theme"), """
            [Icon Theme]
            Name=Test
            Comment=Fixture
            Inherits=Parent
            Directories=16x16/apps,48x48/apps,22x22/apps,scalable/apps
            ScaledDirectories=48x48@2/apps

            [16x16/apps]
            Size=16
            Type=Fixed

            [48x48/apps]
            Size=48
            Type=Fixed

            [48x48@2/apps]
            Size=48
            Scale=2
            Type=Fixed

            [22x22/apps]
            Size=22
            Type=Threshold
            Threshold=4

            [scalable/apps]
            Size=64
            Type=Scalable
            MinSize=56
            MaxSize=512
            """);
        Write(Path.Combine(icons, "Test", "16x16", "apps", "app.png"), Png(16));
        Write(Path.Combine(icons, "Test", "48x48", "apps", "app.png"), Png(48));
        Write(Path.Combine(icons, "Test", "48x48@2", "apps", "app.png"), Png(96));
        Write(Path.Combine(icons, "Test", "22x22", "apps", "app.png"), Png(22));
        Write(Path.Combine(icons, "Test", "scalable", "apps", "app.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        Write(Path.Combine(icons, "Test", "48x48", "apps", "foo-bar.png"), Png(48));
        Write(Path.Combine(icons, "Test", "16x16", "apps", "closest.png"), Png(16));
        Write(Path.Combine(icons, "Test", "48x48", "apps", "closest.png"), Png(48));
        Write(Path.Combine(icons, "Test", "48x48", "apps", "text-x-script.png"), Png(48));
        Write(Path.Combine(icons, "Parent", "index.theme"), "[Icon Theme]\nName=Parent\nComment=P\nDirectories=32x32/apps\n[32x32/apps]\nSize=32\nType=Fixed\n");
        Write(Path.Combine(icons, "Parent", "32x32", "apps", "inherited.png"), Png(32));
        Write(Path.Combine(icons, "Parent", "32x32", "apps", "image-png.png"), Png(32));
        // The user's copy of the theme in ~/.icons extends it.
        Write(Path.Combine(fixture.Home, ".icons", "Test", "48x48", "apps", "home-only.png"), Png(48));
        Write(Path.Combine(icons, "hicolor", "index.theme"), "[Icon Theme]\nName=Hicolor\nComment=H\nDirectories=48x48/apps\n[48x48/apps]\nSize=48\nType=Threshold\n");
        Write(Path.Combine(icons, "hicolor", "48x48", "apps", "third-party.png"), Png(48));
        Write(Path.Combine(icons, "hicolor", "48x48", "apps", "video-x-generic.png"), Png(48));
        Write(Path.Combine(fixture.Pixmaps, "legacy.png"), Png(40));
        Write(Path.Combine(fixture.Share, "mime", "generic-icons"), "text/x-csrc:text-x-script\n");
        Write(Path.Combine(fixture.Share, "mime", "icons"), "image/x-custom:inherited\n");
    }

    private static async Task<(string Media, int Size, int Width)?> IconAsync(LinuxDesktopApplications applications, DesktopIconRequest request)
    {
        var result = await applications.GetIconAsync(request);
        if (result is not PlatformResult<DesktopIconData?>.Success(var icon)) throw new InvalidOperationException(result.ToString());
        if (icon is null) return null;
        int width = icon.MediaType == "image/png" ? icon.Data[16] << 24 | icon.Data[17] << 16 | icon.Data[18] << 8 | icon.Data[19] : -1;
        return (icon.MediaType, icon.Size, width);
    }

    [Test, DisplayName("applications: icons follow the Icon Theme lookup: sizes, scales, thresholds, scalable icons, inheritance, hicolor, pixmaps and dash fallbacks")]
    public async Task IconsAsync()
    {
        using var fixture = new DesktopApplicationsFixture();
        WriteIcons(fixture);
        var applications = fixture.Create(theme: "Test");
        Check(await IconAsync(applications, DesktopIconRequest.ForIconName("app", 48)) == ("image/png", 48, 48), "an exact fixed size");
        Check(await IconAsync(applications, DesktopIconRequest.ForIconName("app", 16)) == ("image/png", 16, 16), "another fixed size");
        Check(await IconAsync(applications, DesktopIconRequest.ForIconName("app", 20)) == ("image/png", 22, 22), "within a threshold");
        Check(await IconAsync(applications, DesktopIconRequest.ForIconName("app", 100)) == ("image/svg+xml", 100, -1), "a scalable icon at the requested size");
        Check(await IconAsync(applications, DesktopIconRequest.ForIconName("app", 48, scale: 2)) == ("image/png", 48, 96), "the scaled directory");
        Check(await IconAsync(applications, DesktopIconRequest.ForIconName("closest", 40)) == ("image/png", 48, 48), "the closest size when none matches");
        Check(await IconAsync(applications, DesktopIconRequest.ForIconName("inherited", 48)) == ("image/png", 32, 32), "from the inherited theme");
        Check(await IconAsync(applications, DesktopIconRequest.ForIconName("third-party", 48)) == ("image/png", 48, 48), "from hicolor");
        Check(await IconAsync(applications, DesktopIconRequest.ForIconName("home-only", 48)) == ("image/png", 48, 48), "from ~/.icons");
        Check(await IconAsync(applications, DesktopIconRequest.ForIconName("legacy", 48)) == ("image/png", 40, 40), "unthemed from pixmaps, sized by its header");
        Check(await IconAsync(applications, DesktopIconRequest.ForIconName("foo-bar-baz", 48)) == ("image/png", 48, 48), "foo-bar-baz falls back to foo-bar");
        Check(await IconAsync(applications, DesktopIconRequest.ForIconName("nothing-at-all", 48)) is null, "a successful null without an icon");
        Check(await IconAsync(fixture.Create(), DesktopIconRequest.ForIconName("app", 48)) is null
            && await IconAsync(fixture.Create(), DesktopIconRequest.ForIconName("third-party", 48)) is not null, "without a theme only hicolor applies");

        Check(await IconAsync(applications, DesktopIconRequest.ForContentType("text/x-csrc", 48)) == ("image/png", 48, 48), "a content type's generic icon");
        Check(await IconAsync(applications, DesktopIconRequest.ForContentType("image/png", 32)) == ("image/png", 32, 32), "media-subtype");
        Check(await IconAsync(applications, DesktopIconRequest.ForContentType("image/x-custom", 32)) == ("image/png", 32, 32), "the icons file");
        Check(await IconAsync(applications, DesktopIconRequest.ForContentType("video/x-unknown", 48)) == ("image/png", 48, 48), "media-x-generic");
        Check(await applications.GetIconAsync(DesktopIconRequest.ForContentType("bogus", 48)) is PlatformResult<DesktopIconData?>.Failed { Code: PlatformFailureCode.InvalidData }, "a malformed type");

        string absolute = Write(Path.Combine(fixture.Root, "own", "icon.png"), Png(64));
        Entry(fixture.Share, "named.desktop", "Named", "Icon=app.png\n");
        Entry(fixture.Share, "absolute.desktop", "Absolute", $"Icon={absolute}\n");
        Entry(fixture.Share, "absolute-xpm.desktop", "XPM", $"Icon={Path.Combine(fixture.Root, "own", "icon.xpm")}\n");
        Entry(fixture.Share, "iconless.desktop", "Iconless");
        Check(await IconAsync(applications, DesktopIconRequest.ForApplication("named.desktop", 48)) == ("image/png", 48, 48), "an entry's icon name, without its extension");
        Check(await IconAsync(applications, DesktopIconRequest.ForApplication("absolute.desktop", 48)) == ("image/png", 64, 64), "an absolute PNG path");
        Check(await IconAsync(applications, DesktopIconRequest.ForApplication("absolute-xpm.desktop", 48)) is null
            && await IconAsync(applications, DesktopIconRequest.ForApplication("iconless.desktop", 48)) is null, "no XPM, no icon");
        Check(await applications.GetIconAsync(DesktopIconRequest.ForApplication("unknown.desktop", 48)) is PlatformResult<DesktopIconData?>.Failed { Code: PlatformFailureCode.InvalidData }, "an unknown application");

        Write(Path.Combine(fixture.Share, "icons", "Test", "48x48", "apps", "huge.png"), new byte[(4 << 20) + 1]);
        Check(await applications.GetIconAsync(DesktopIconRequest.ForIconName("huge", 48)) is PlatformResult<DesktopIconData?>.Failed { Code: PlatformFailureCode.TooLarge }, "over 4 MiB");

        Write(Path.Combine(fixture.ConfigHome, "kdeglobals"), "[General]\nName=x\n[Icons]\nTheme=Test\n");
        Check(await IconAsync(fixture.Create(), DesktopIconRequest.ForIconName("app", 48)) == ("image/png", 48, 48), "KDE's kdeglobals names the theme");
        Check(new IconThemeLookup(fixture.Options(), "Test").ThemeNames.SequenceEqual(["Test", "Parent", "hicolor"]), "the theme, its parents, then hicolor");
        Check(IconThemeLookup.DashFallbacks("a-b-c").SequenceEqual(["a-b-c", "a-b", "a"]) && !IconThemeLookup.IsThemeName("../x") && !IconThemeLookup.IsThemeName("a,b"), "names");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
