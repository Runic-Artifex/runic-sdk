namespace Runic.Application.Tool;

internal sealed record DoctorOptions(
    string? Project,
    string Configuration,
    DoctorTargetRid? Target = null,
    bool? Aot = null,
    bool? SelfContained = null)
{
    /// <summary><c>--bundle</c>: a web bundle to check offline (split CI F6), or null.</summary>
    internal string? Bundle { get; init; }
}
