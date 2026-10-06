namespace Runic.Application.Tool;

internal sealed record DoctorOptions(
    string? Project,
    string Configuration,
    DoctorTargetRid? Target = null,
    bool Aot = false,
    bool SelfContained = false);
