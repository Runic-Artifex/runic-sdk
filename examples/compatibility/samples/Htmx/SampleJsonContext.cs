using System.Text.Json.Serialization;

namespace WebUIToolkit.Htmx.Sample;

[JsonSerializable(typeof(string))]
internal sealed partial class SampleJsonContext : JsonSerializerContext;
