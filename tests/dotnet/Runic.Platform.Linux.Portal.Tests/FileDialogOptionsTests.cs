using Runic.Platform;
using Runic.Platform.Linux.Portal;
using Runic.Platform.Runtime;
using TUnit.Core;
using Tmds.DBus.Protocol;
using DBusValue = Runic.Platform.Linux.DBus.DBusValue;

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

        // #713: the application's title replaces Runic's default title argument of every kind of dialog.
        transport = new FakeTransport(new(1, []));
        await new PortalFilePicker(new Owner(), transport).SelectAsync(new NativeFileDialogRequest(false, null) { Title = "Import backup" }, default);
        Check(transport.Chooser is { Title: "Import backup" } && PortalChooserOptions.DialogTitle("OpenFile", transport.Chooser) == "Import backup",
            "open title");
        transport = new FakeTransport(new(1, []));
        await new PortalFilePicker(new Owner(), transport).SelectAsync(new NativeFileDialogRequest(true, "backup.zip") { Title = "Export backup" }, default);
        Check(PortalChooserOptions.DialogTitle("SaveFile", transport.Chooser) == "Export backup", "save title");
        transport = new FakeTransport(new(1, []));
        await ((INativeDirectoryPicker)new PortalFilePicker(new Owner(), transport))
            .SelectDirectoryAsync(new NativeFileDialogRequest(false, null) { Title = "Log folder" }, default);
        Check(transport.Method == "SelectDirectory" && PortalChooserOptions.DialogTitle("SelectDirectory", transport.Chooser) == "Log folder", "directory title");
        Check(PortalChooserOptions.DialogTitle("OpenFile", null) == "Open file" && PortalChooserOptions.DialogTitle("SaveFile", null) == "Save file"
            && PortalChooserOptions.DialogTitle("SelectDirectory", PortalChooserOptions.From([], null, true)) == "Open directories",
            "Runic's default titles");
    }

    [Test, DisplayName("portal filters carry MIME types; current_filter names the chosen filter")]
    public async Task MimeAndChosenFilterAsync()
    {
        var pictures = new FileDialogFilter("Pictures", ["png"]) { MimeTypes = ["image/*", "Image/WebP"] };
        var text = new FileDialogFilter("Text", ["txt"]);
        var otherText = new FileDialogFilter("Text", ["md"]);
        Check(Describe(PortalChooserOptions.Filter(pictures)) == "Pictures:0=*.[pP][nN][gG],1=image/*,1=image/webp", "MIME types follow the globs as (1, type)");
        Check(Describe(PortalChooserOptions.Filter(Everything with { MimeTypes = ["text/plain"] })) == "All files:0=*", "an all-files filter needs no MIME type");
        Check(PortalChooserOptions.Chosen([pictures, text], PortalChooserOptions.Filter(text)) == text, "the same filter");
        Check(PortalChooserOptions.Chosen([pictures, text, otherText], PortalChooserOptions.Filter(otherText)) == otherText, "the same name, told apart by patterns");
        Check(PortalChooserOptions.Chosen([text, otherText], PortalChooserOptions.Filter(new("Text", ["rst"]))) is null, "an ambiguous name");
        Check(PortalChooserOptions.Chosen([pictures, text], PortalChooserOptions.Filter(new("Text", ["log"]))) == text, "a backend's own patterns under a unique name");
        Check(PortalChooserOptions.Chosen([pictures], DBusValue.From("Pictures")) is null && PortalChooserOptions.Chosen([], PortalChooserOptions.Filter(text)) is null
            && PortalChooserOptions.Chosen([pictures], null) is null, "no filter");

        var transport = new FakeTransport(new(0, ["file:///tmp/a.txt"]) { CurrentFilter = PortalChooserOptions.Filter(text) });
        var selections = await new PortalFilePicker(new Owner(), transport).SelectAsync(new NativeFileDialogRequest(false, null) { Filters = [pictures, text] }, default);
        Check(selections is [{ SelectedFilter: var chosen }] && chosen == text, "the picker reports the portal's current_filter");

        transport = new FakeTransport(new(0, ["file:///tmp/a", "file:///tmp/b%20c"]));
        var picker = new PortalFilePicker(new Owner(), transport);
        Check(picker.SupportsMultipleDirectorySelection, "the portal picker declares multiple directory selection");
        var directories = await picker.SelectDirectoriesAsync("/tmp", default);
        Check(transport.Method == "SelectDirectory" && transport.Chooser is { Multiple: true, CurrentFolder: "/tmp" } && directories is [{ Path: "/tmp/a" }, { Path: "/tmp/b c" }],
            "directory and multiple");
        Check(await new PortalFilePicker(new Owner(), new FakeTransport(new(1, []))).SelectDirectoriesAsync(null, default) is null, "a dismissed directory dialog");
    }

    private static string Describe(DBusValue filter)
    {
        var fields = filter.GetFields();
        return fields[0].GetString() + ":" + string.Join(',', fields[1].GetItems().Select(static item => item.GetFields()).Select(static pair => $"{pair[0].GetUInt32()}={pair[1].GetString()}"));
    }

    [Test, Explicit, Category("DBus"), DisplayName("real D-Bus protocol: FileChooser MIME filters, current_filter in the reply, directories with multiple")]
    public async Task WireChosenFilterAsync()
    {
        using var service = new DBusConnection(DBusAddress.Session!);
        await service.ConnectAsync();
        var fake = new PortalService(service) { Uris = ["file:///tmp/a.png"], RespondFilter = 1 };
        service.AddMethodHandler(fake);
        var transport = new PortalTransport(destination: service.UniqueName!);
        var pictures = new FileDialogFilter("Pictures", ["png"]) { MimeTypes = ["image/*"] };
        var selections = await new PortalFilePicker(new Owner(), transport)
            .SelectAsync(new NativeFileDialogRequest(false, null) { Filters = [Images, pictures] }, default);
        Check(Filter(fake.Options["filters"].GetItem(1)) == "Pictures:0=*.[pP][nN][gG],1=image/*", "a MIME type is (1, type) on the wire");
        Check(selections is [{ SelectedFilter: var chosen }] && chosen == pictures, "current_filter in the reply is the chosen filter");

        fake.RespondFilter = null;
        fake.Uris = ["file:///tmp/one", "file:///tmp/two"];
        var directories = await new PortalFilePicker(new Owner(), transport).SelectDirectoriesAsync("/tmp", default);
        Check(fake.Call == "org.freedesktop.portal.FileChooser.OpenFile(ssa{sv}) handle_token,modal,current_folder,multiple,directory" && fake.Argument == "Open directories"
            && directories is [{ Path: "/tmp/one" }, { Path: "/tmp/two" }], $"directories: {fake.Call}");
        Console.WriteLine("PASS FileChooser MIME filters, current_filter and multiple directories on the wire.");
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
