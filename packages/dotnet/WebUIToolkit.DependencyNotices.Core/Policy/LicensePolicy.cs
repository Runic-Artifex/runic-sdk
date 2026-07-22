using System;
using System.Collections.Generic;

namespace WebUIToolkit.DependencyNotices.Policy;

public enum LicensePolicyOutcome
{
    Allow,
    Review,
    Deny,
}

public sealed record LicensePolicy(
    IReadOnlySet<string> Allowed,
    IReadOnlySet<string> Denied,
    IReadOnlySet<string> Review,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Obligations,
    LicensePolicyOutcome DefaultOutcome = LicensePolicyOutcome.Review,
    bool RequireExplicitOrSelection = true)
{
    public static LicensePolicy Create(
        IEnumerable<string>? allowed = null,
        IEnumerable<string>? denied = null,
        IEnumerable<string>? review = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? obligations = null,
        LicensePolicyOutcome defaultOutcome = LicensePolicyOutcome.Review,
        bool requireExplicitOrSelection = true) =>
        new(
            new HashSet<string>(allowed ?? [], StringComparer.Ordinal),
            new HashSet<string>(denied ?? [], StringComparer.Ordinal),
            new HashSet<string>(review ?? [], StringComparer.Ordinal),
            obligations ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            defaultOutcome,
            requireExplicitOrSelection);
}
