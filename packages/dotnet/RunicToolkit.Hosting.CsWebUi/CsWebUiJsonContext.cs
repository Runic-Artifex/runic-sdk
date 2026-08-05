using System.Text.Json.Serialization;

namespace RunicToolkit.Hosting.CsWebUi;

[JsonSerializable(typeof(string))]
internal sealed partial class CsWebUiJsonContext : JsonSerializerContext;
