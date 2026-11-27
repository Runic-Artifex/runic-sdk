using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Application.Views;
using Runic.Application.Views.Generated;

namespace Runic.Application.Testing.Tests.Conformance;

// The files View (specs/application/conformance/README.md, "Test Views"): blobs/1. avatar is a nullable blob that the
// model and the assign mutation give as {type, text, name}, the text being its UTF-8 content; attach takes an upload and
// claims the upload and sets lastUpload to its file name. attachments are editable rows that hold blobs: their name and
// their cover's caption are field paths; their preview, their cover, its image and their pages are not, because each
// holds a blob, which the page never sends (runic-sdk#1391, contract-ir.md "Field paths").
internal sealed record FilesCover(string Caption, RunicBlob? Image);

internal sealed record FilesAttachment(int Id, string Name, RunicBlob Preview, FilesCover Cover, IReadOnlyList<RunicBlob> Pages)
{
    internal static void Write(Utf8JsonWriter writer, FilesAttachment row)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", row.Id);
        writer.WriteString("name", row.Name);
        writer.WritePropertyName("preview");
        BridgeWire.WriteBlob(writer, row.Preview);
        writer.WritePropertyName("cover");
        writer.WriteStartObject();
        writer.WriteString("caption", row.Cover.Caption);
        writer.WritePropertyName("image");
        if (row.Cover.Image is { } image) BridgeWire.WriteBlob(writer, image);
        else writer.WriteNullValue();
        writer.WriteEndObject();
        writer.WritePropertyName("pages");
        writer.WriteStartArray();
        foreach (var page in row.Pages) BridgeWire.WriteBlob(writer, page);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    internal static string KeyOf(object? row) => ((FilesAttachment)row!).Id.ToString(CultureInfo.InvariantCulture);
}

internal sealed class FilesViewModel : ConformanceModel
{
    private RunicBlob? _avatar;
    private string? _lastUpload;

    private FilesViewModel() =>
        AttachCommand = new(_ => true, input =>
        {
            // Claims the upload, so a later request that names it is invalid input (README, "Uploads").
            var upload = (RunicUpload)input!;
            upload.Discard();
            LastUpload = upload.FileName;
        });

    public RunicBlob? Avatar
    {
        get => _avatar;
        private set { _avatar = value; Raise(); }
    }

    public string? LastUpload
    {
        get => _lastUpload;
        private set { _lastUpload = value; Raise(); }
    }

    public ObservableCollection<FilesAttachment> Attachments { get; } = [];

    public ConformanceCommand AttachCommand { get; }

    // The row editor: replaces the attachment with its key.
    internal void UpdateAttachment(int id, FilesAttachment row)
    {
        for (var index = 0; index < Attachments.Count; index++)
        {
            if (Attachments[index].Id != id) continue;
            Attachments[index] = row;
            return;
        }
        throw new InvalidOperationException($"No attachment {id}.");
    }

    internal static FilesViewModel Create(JsonObject model)
    {
        var vm = new FilesViewModel();
        if (model.ContainsKey("avatar")) vm._avatar = Blob(model["avatar"]);
        if (model["attachments"] is JsonArray attachments)
            foreach (var row in attachments)
                vm.Attachments.Add(new(row!["id"]!.GetValue<int>(), row["name"]!.GetValue<string>(), Blob(row["preview"])!,
                    new(row["cover"]!["caption"]!.GetValue<string>(), Blob(row["cover"]!["image"])),
                    [.. row["pages"]!.AsArray().Select(page => Blob(page)!)]));
        return vm;
    }

    private static RunicBlob? Blob(JsonNode? value) => value is null ? null
        : RunicBlob.FromBytes(Encoding.UTF8.GetBytes(value["text"]!.GetValue<string>()), value["type"]!.GetValue<string>(),
            value["name"]?.GetValue<string>());

    internal override void Apply(JsonObject mutation, Func<IDisposable>? unit)
    {
        if (mutation["op"]!.GetValue<string>() == "assign" && mutation["member"]!.GetValue<string>() == "avatar")
            Avatar = Blob(mutation["value"]);
        else throw new NotSupportedException($"The files View has no mutation {mutation.ToJsonString()}.");
    }

    internal override JsonNode? Read(string member) => member switch
    {
        "lastUpload" => LastUpload,
        _ => throw new NotSupportedException($"The files View has no member {member}."),
    };
}

internal sealed class FilesBridge(IBridgeTransport transport, FilesViewModel vm, WindowContentSession content)
    : ViewModelBridge(transport, vm, typeof(FilesViewModel), "files",
        [
            new("avatar", static (writer, model) =>
            {
                if (((FilesViewModel)model).Avatar is { } avatar) BridgeWire.WriteBlob(writer, avatar);
                else writer.WriteNullValue();
            }),
            new("lastUpload", ConformanceWire.Value<FilesViewModel>(model => model.LastUpload)),
            new("attachments", static (writer, model) =>
            {
                writer.WriteStartArray();
                foreach (var row in ((FilesViewModel)model).Attachments) FilesAttachment.Write(writer, row);
                writer.WriteEndArray();
            }),
        ],
        [
            new("Avatar", "avatar", model => ((FilesViewModel)model).Avatar, null),
            new("LastUpload", "lastUpload", model => ((FilesViewModel)model).LastUpload, null) { TextMember = true },
            new("Attachments", "attachments", model => ((FilesViewModel)model).Attachments, null),
        ],
        [],
        [new CommandDescriptor("Attach", model => ((FilesViewModel)model).AttachCommand) { ReadInput = static element => BridgeWire.ReadUpload(element) }],
        ConformanceContracts.Fingerprint(typeof(FilesViewModel)), content,
        collections:
        [
            new("attachments", model => ((FilesViewModel)model).Attachments, (writer, row) => FilesAttachment.Write(writer, (FilesAttachment)row!), FilesAttachment.KeyOf)
            {
                // preview, cover, cover.image and pages hold blobs, so they are not field paths. A copy keeps the blobs.
                FieldPaths =
                [
                    new(["name"], (row, value) => (FilesAttachment)row with { Name = ConformanceWire.String(value) }),
                    new(["cover", "caption"], (row, value) => (FilesAttachment)row with
                    {
                        Cover = ((FilesAttachment)row).Cover with { Caption = ConformanceWire.String(value) },
                    }),
                ],
                UpdateRow = (model, row, _) => ((FilesViewModel)model).UpdateAttachment(((FilesAttachment)row!).Id, (FilesAttachment)row),
            },
        ]);
