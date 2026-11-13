using System.Text;
using System.Text.Json.Nodes;
using Runic.Application.Views;
using Runic.Application.Views.Generated;

namespace Runic.Application.Testing.Tests.Conformance;

// The files View (specs/application/conformance/README.md, "Test Views"): blobs/1. avatar is a nullable blob that the
// model and the assign mutation give as {type, text, name}, the text being its UTF-8 content; attach takes an upload and
// sets lastUpload to the upload's file name.
internal sealed class FilesViewModel : ConformanceModel
{
    private RunicBlob? _avatar;
    private string? _lastUpload;

    private FilesViewModel() =>
        AttachCommand = new(_ => true, upload => LastUpload = ((RunicUpload)upload!).FileName);

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

    public ConformanceCommand AttachCommand { get; }

    internal static FilesViewModel Create(JsonObject model)
    {
        var vm = new FilesViewModel();
        if (model.ContainsKey("avatar")) vm._avatar = Blob(model["avatar"]);
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
        ],
        [new("Avatar", "avatar", model => ((FilesViewModel)model).Avatar, null), new("LastUpload", "lastUpload", model => ((FilesViewModel)model).LastUpload, null)],
        [],
        [new CommandDescriptor("Attach", model => ((FilesViewModel)model).AttachCommand) { ReadInput = static element => BridgeWire.ReadUpload(element) }],
        "conformance-files", content);
