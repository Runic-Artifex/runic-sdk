using System.Reflection;
using System.Text.Json.Serialization;

namespace Runic.Application.Views;

// One exclusion rule for the build-time codecs, their subscription/validation
// metadata, and the reflection shape used by Hot Reload. The opt-in belongs to
// the model assembly, not to each DTO's assembly.
internal static class BridgeDtoContract
{
    internal static bool HonorsJsonIgnore(Assembly modelAssembly) =>
        modelAssembly.IsDefined(typeof(RunicBridgeJsonIgnoreAttribute), inherit: false);

    internal static bool IsIgnored(PropertyInfo property, bool honorJsonIgnore) =>
        property.GetCustomAttribute<RunicIgnoreAttribute>(inherit: true) is not null ||
        honorJsonIgnore && property.GetCustomAttribute<JsonIgnoreAttribute>(inherit: true)?.Condition == JsonIgnoreCondition.Always;
}
