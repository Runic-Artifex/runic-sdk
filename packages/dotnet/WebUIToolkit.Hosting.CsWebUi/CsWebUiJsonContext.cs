using System.Text.Json.Serialization;

namespace WebUIToolkit.Hosting.CsWebUi;

[JsonSerializable(typeof(string))]
internal sealed partial class CsWebUiJsonContext : JsonSerializerContext;
