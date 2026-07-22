using System.Text.Json.Serialization;

namespace WebUIToolkit.DependencyNotices.Runtime.Serialization;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = false,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(NoticeDocumentJson))]
internal sealed partial class NoticeJsonContext : JsonSerializerContext
{
}
