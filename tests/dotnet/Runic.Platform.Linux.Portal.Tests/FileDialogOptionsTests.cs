using Runic.Platform;
using Runic.Platform.Linux.Portal;
using Runic.Platform.Runtime;
using TUnit.Core;
using Tmds.DBus.Protocol;

// Filters, the initial folder and multiple selection reach the FileChooser portal as its documented options.
internal sealed class FileDialogOptionsTests
{
    private static readonly FileDialogFilter Images = new("Images", ["png", "JPG"]);
    private static readonly FileDialogFilter Archives = new("Archives", ["tar.gz", "7z"]);
    private static readonly FileDialogFilter Everything = new("All files", ["*"]);

    [Test, DisplayName("portal filter globs match extensions case-insensitively")]
    public void Globs()
    {
        Check(FileDialogPatterns.Globs(Images).SequenceEqual(["*.[pP][nN][gG]", "*.[jJ][pP][gG]"]), "letters match either case");
        Check(FileDialogPatterns.Globs(Archives).SequenceEqual(["*.[tT][aA][rR].[gG][zZ]", "*.7[zZ]"]), "digits and dots are literal");
        Check(FileDialogPatterns.Globs(Everything).SequenceEqual(["*"]), "every file");
    }

    [Test, DisplayName("portal picker passes filters, folder and multiplicity, and returns every selected file")]
    public async Task PickerRequestAsync()
    {
        var transport = new FakeTransport(new(0, ["file:///tmp/a.png", "file:///tmp/b%20c.jpg"]));
        var picker = new PortalFilePicker(new Owner(), transport);
        var selections = await picker.SelectAsync(new NativeFileDialogRequest(false, null)
        {
            Filters = [Images, Everything], InitialDirectory = "/tmp/pictures", AllowMultiple = true,
        }, default);
        Check(transport.Method == "OpenFile" && transport.Chooser is { Multiple: true, CurrentFolder: "/tmp/pictures" } chooser
            && chooser.Filters.SequenceEqual([Images, Everything]), "open request options");
        Check(selections is [{ Path: "/tmp/a.png" }, { Path: "/tmp/b c.jpg" }], "every selected file is returned in order");

        transport = new FakeTransport(new(0, ["file:///tmp/a.png", "file:///tmp/b.png"]));
        await Throws<IOException>(() => new PortalFilePicker(new Owner(), transport).SelectAsync(new NativeFileDialogRequest(false, null), default).AsTask());
        transport = new FakeTransport(new(0, []));
        await Throws<IOException>(() => new PortalFilePicker(new Owner(), transport)
            .SelectAsync(new NativeFileDialogRequest(false, null) { AllowMultiple = true }, default).AsTask());
        transport = new FakeTransport(new(0, ["file:///tmp/a.png", "https://example.org/b.png"]));
        await Throws<IOException>(() => new PortalFilePicker(new Owner(), transport)
            .SelectAsync(new NativeFileDialogRequest(false, null) { AllowMultiple = true }, default).AsTask());

        // A dialog without options keeps the wire request it always sent.
        transport = new FakeTransport(new(1, []));
        Check(await new PortalFilePicker(new Owner(), transport).SelectAsync(false, null, default) is null && transport.Chooser is null,
            "no options without a request for them");
        // A save dialog is never multiple; it keeps filters and folder.
        transport = new FakeTransport(new(1, []));
        await new PortalFilePicker(new Owner(), transport).SelectAsync(new NativeFileDialogRequest(true, "notes.txt")
        {
            Filters = [Archives], InitialDirectory = "/tmp", AllowMultiple = true,
        }, default);
        Check(transport.Method == "SaveFile" && transport.Chooser is { Multiple: false, CurrentFolder: "/tmp" } save && save.Filters.SequenceEqual([Archives]),
            "save request options");
        transport = new FakeTransport(new(1, []));
        await new PortalFilePicker(new Owner(), transport).SelectDirectoryAsync("/tmp", default);
        Check(transport.Method == "SelectDirectory" && transport.Chooser is { CurrentFolder: "/tmp", Multiple: false } directory && directory.Filters.IsEmpty,
            "directory request folder");
        Check(new PortalFilePicker(new Owner(), transport).SupportsMultipleSelection, "the portal picker declares multiple selection");
    }

    [Test, Explicit, Category("DBus"), DisplayName("real D-Bus protocol: FileChooser filters, current_filter, current_folder and multiple")]
    public async Task WireOptionsAsync()
    {
        using var service = new DBusConnection(DBusAddress.Session!);
        await service.ConnectAsync();
        var fake = new PortalService(service) { Uris = ["file:///tmp/a.png", "file:///tmp/b.jpg"] };
        service.AddMethodHandler(fake);
        var transport = new PortalTransport(destination: service.UniqueName!);
        const string folder = "/tmp/Bilder Ω";
        var result = await transport.RequestAsync("x11:1234", "OpenFile", "", new PortalChooserOptions([Images, Everything], folder, true), default);
        Check(result.Code == 0 && result.Uris.Length == 2, "multiple URIs returned");
        Check(fake.Argument == "Open files" && fake.Call == "org.freedesktop.portal.FileChooser.OpenFile(ssa{sv}) handle_token,modal,filters,current_filter,current_folder,multiple",
            $"OpenFile option names: {fake.Call}");
        var filters = fake.Options["filters"];
        Check(filters.Count == 2 && Filter(filters.GetItem(0)) == "Images:0=*.[pP][nN][gG],0=*.[jJ][pP][gG]" && Filter(filters.GetItem(1)) == "All files:0=*",
            "filters are (name, [(0, glob)])");
        Check(Filter(fake.Options["current_filter"]) == "Images:0=*.[pP][nN][gG],0=*.[jJ][pP][gG]", "the first filter is current");
        Check(fake.Options["current_folder"].GetArray<byte>().SequenceEqual([.. System.Text.Encoding.UTF8.GetBytes(folder), (byte)0]),
            "current_folder is the path's NUL-terminated bytes");
        Check(fake.Options["multiple"].GetBool(), "multiple is true");

        var selections = await new PortalFilePicker(new Owner(), transport)
            .SelectAsync(new NativeFileDialogRequest(false, null) { AllowMultiple = true }, default);
        Check(selections is [{ Path: "/tmp/a.png" }, { Path: "/tmp/b.jpg" }], "the picker returns both portal URIs");

        fake.Uris = ["file:///tmp/saved.tar.gz"];
        await transport.RequestAsync("x11:1234", "SaveFile", "saved.tar.gz", new PortalChooserOptions([Archives], "/tmp", false), default);
        Check(fake.Call == "org.freedesktop.portal.FileChooser.SaveFile(ssa{sv}) handle_token,modal,filters,current_filter,current_folder,current_name"
            && !fake.Options.ContainsKey("multiple") && Filter(fake.Options["current_filter"]) == "Archives:0=*.[tT][aA][rR].[gG][zZ],0=*.7[zZ]",
            $"SaveFile options: {fake.Call}");
        Console.WriteLine("PASS FileChooser filters, current_filter, current_folder and multiple on the wire.");
    }

    // "name:type=pattern,type=pattern" of one (sa(us)) filter.
    private static string Filter(VariantValue filter)
    {
        var patterns = filter.GetItem(1);
        return filter.GetItem(0).GetString() + ":" + string.Join(',', Enumerable.Range(0, patterns.Count)
            .Select(index => patterns.GetItem(index)).Select(pattern => $"{pattern.GetItem(0).GetUInt32()}={pattern.GetItem(1).GetString()}"));
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
