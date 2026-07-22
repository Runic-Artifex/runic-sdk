using System;

namespace WebUIToolkit.DependencyNotices.Acquisition;

public sealed record AcquisitionRequest(
    AcquisitionOperation Operation,
    bool AllowNetwork,
    Uri Origin,
    string ExpectedSha256);

public sealed record AcquisitionResult(
    Uri Origin,
    Uri EffectiveOrigin,
    string Sha256,
    string CachePath,
    long ByteCount,
    int RedirectCount,
    bool WasAlreadyCached);
