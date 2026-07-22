using System;
using System.Collections.Generic;
using System.Linq;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Sbom;

public static class SbomReconciler
{
    public static SbomReconciliationResult Reconcile(
        IEnumerable<SbomInventoryIdentity> inventory,
        SbomDocument sbom)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(sbom);

        List<SbomInventoryIdentity> expected = inventory.ToList();
        expected.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.PackageUrl.CanonicalValue, right.PackageUrl.CanonicalValue));
        List<SbomComponent> actual = sbom.Components.ToList();
        actual.Sort(SbomComponentComparer.Instance);

        List<NoticeDiagnostic> diagnostics = [];
        HashSet<int> unavailable = FindDuplicateReferences(actual, sbom.DocumentReference, diagnostics);
        HashSet<int> matchedActual = [];
        List<SbomComponentLink> links = [];

        foreach (SbomInventoryIdentity item in expected)
        {
            int match = FindExactPackageUrl(actual, unavailable, matchedActual, item.PackageUrl.CanonicalValue);
            if (match < 0)
            {
                match = FindFallback(actual, unavailable, matchedActual, expected, item);
            }

            if (match >= 0)
            {
                matchedActual.Add(match);
                links.Add(new SbomComponentLink(item.PackageUrl.CanonicalValue, actual[match].ComponentReference));
                continue;
            }

            int mismatch = FindIdentityMismatch(actual, unavailable, matchedActual, item);
            if (mismatch >= 0)
            {
                matchedActual.Add(mismatch);
                SbomComponent component = actual[mismatch];
                diagnostics.Add(Diagnostic(
                    NoticeDiagnosticCodes.SbomIdentityMismatch,
                    $"Inventory component '{item.PackageUrl.CanonicalValue}' conflicts with SBOM component '{component.ComponentReference}' ({Describe(component)}).",
                    item.PackageUrl.CanonicalValue,
                    sbom.DocumentReference));
            }
            else
            {
                diagnostics.Add(Diagnostic(
                    NoticeDiagnosticCodes.SbomComponentMissing,
                    $"Inventory component '{item.PackageUrl.CanonicalValue}' is missing from SBOM '{sbom.DocumentReference}'.",
                    item.PackageUrl.CanonicalValue,
                    sbom.DocumentReference));
            }
        }

        for (int index = 0; index < actual.Count; index++)
        {
            if (matchedActual.Contains(index) || unavailable.Contains(index))
            {
                continue;
            }

            SbomComponent component = actual[index];
            diagnostics.Add(Diagnostic(
                NoticeDiagnosticCodes.SbomComponentExtra,
                $"SBOM component '{component.ComponentReference}' ({Describe(component)}) is not present in the inventory.",
                component.PackageUrl?.CanonicalValue,
                sbom.DocumentReference));
        }

        links.Sort(static (left, right) =>
        {
            int result = StringComparer.Ordinal.Compare(left.PackageUrl, right.PackageUrl);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.ComponentReference, right.ComponentReference);
        });
        diagnostics.Sort(NoticeDiagnosticComparer.Instance);
        return new SbomReconciliationResult(sbom.Format, sbom.DocumentReference, sbom.SerialNumber, links.AsReadOnly(), diagnostics.AsReadOnly());
    }

    private static HashSet<int> FindDuplicateReferences(
        List<SbomComponent> components,
        string documentReference,
        List<NoticeDiagnostic> diagnostics)
    {
        Dictionary<string, List<int>> byReference = new(StringComparer.Ordinal);
        for (int index = 0; index < components.Count; index++)
        {
            string reference = components[index].ComponentReference;
            if (!byReference.TryGetValue(reference, out List<int>? indices))
            {
                indices = [];
                byReference.Add(reference, indices);
            }

            indices.Add(index);
        }

        HashSet<int> unavailable = [];
        foreach (KeyValuePair<string, List<int>> pair in byReference.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (pair.Value.Count < 2)
            {
                continue;
            }

            foreach (int index in pair.Value)
            {
                unavailable.Add(index);
            }

            diagnostics.Add(Diagnostic(
                NoticeDiagnosticCodes.DuplicateSbomReference,
                $"SBOM component reference '{pair.Key}' occurs {pair.Value.Count} times.",
                null,
                documentReference));
        }

        return unavailable;
    }

    private static int FindExactPackageUrl(List<SbomComponent> components, HashSet<int> unavailable, HashSet<int> matched, string purl)
    {
        int result = -1;
        for (int index = 0; index < components.Count; index++)
        {
            if (unavailable.Contains(index) || matched.Contains(index) ||
                !StringComparer.Ordinal.Equals(components[index].PackageUrl?.CanonicalValue, purl))
            {
                continue;
            }

            if (result >= 0)
            {
                return -1;
            }

            result = index;
        }

        return result;
    }

    private static int FindFallback(
        List<SbomComponent> components,
        HashSet<int> unavailable,
        HashSet<int> matched,
        List<SbomInventoryIdentity> inventory,
        SbomInventoryIdentity item)
    {
        int inventoryCandidates = 0;
        foreach (SbomInventoryIdentity candidate in inventory)
        {
            if (SameFallbackIdentity(candidate, item.PackageUrl.Type, item.Name, item.Version))
            {
                inventoryCandidates++;
            }
        }

        if (inventoryCandidates != 1)
        {
            return -1;
        }

        int result = -1;
        for (int index = 0; index < components.Count; index++)
        {
            SbomComponent component = components[index];
            if (unavailable.Contains(index) || matched.Contains(index) || component.PackageUrl is not null ||
                !SameFallbackIdentity(item, component.Ecosystem, component.Name, component.Version))
            {
                continue;
            }

            if (result >= 0)
            {
                return -1;
            }

            result = index;
        }

        return result;
    }

    private static int FindIdentityMismatch(List<SbomComponent> components, HashSet<int> unavailable, HashSet<int> matched, SbomInventoryIdentity item)
    {
        int result = -1;
        for (int index = 0; index < components.Count; index++)
        {
            SbomComponent candidate = components[index];
            string? ecosystem = candidate.PackageUrl?.Type ?? candidate.Ecosystem;
            if (unavailable.Contains(index) || matched.Contains(index) ||
                !StringComparer.OrdinalIgnoreCase.Equals(ecosystem, item.PackageUrl.Type) ||
                !NameEquals(item.PackageUrl.Type, candidate.Name, item.Name) ||
                (candidate.PackageUrl is null && StringComparer.Ordinal.Equals(candidate.Version, item.Version)))
            {
                continue;
            }

            if (result >= 0)
            {
                return -1;
            }

            result = index;
        }

        return result;
    }

    private static bool SameFallbackIdentity(SbomInventoryIdentity inventory, string? ecosystem, string name, string version) =>
        ecosystem is not null &&
        StringComparer.OrdinalIgnoreCase.Equals(ecosystem, inventory.PackageUrl.Type) &&
        NameEquals(inventory.PackageUrl.Type, name, inventory.Name) &&
        StringComparer.Ordinal.Equals(version, inventory.Version);

    private static bool NameEquals(string ecosystem, string left, string right) =>
        StringComparer.OrdinalIgnoreCase.Equals(ecosystem, "nuget")
            ? StringComparer.OrdinalIgnoreCase.Equals(left, right)
            : StringComparer.Ordinal.Equals(left, right);

    private static string Describe(SbomComponent component) =>
        component.PackageUrl?.CanonicalValue ?? $"{component.Ecosystem ?? "unknown"}:{component.Name}@{component.Version}";

    private static NoticeDiagnostic Diagnostic(string code, string message, string? purl, string source) =>
        new(code, NoticeDiagnosticSeverity.Error, message, purl, source);

    private sealed class SbomComponentComparer : IComparer<SbomComponent>
    {
        public static SbomComponentComparer Instance { get; } = new();

        public int Compare(SbomComponent? left, SbomComponent? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            int result = StringComparer.Ordinal.Compare(left.PackageUrl?.CanonicalValue, right.PackageUrl?.CanonicalValue);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.Ecosystem, right.Ecosystem);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.Name, right.Name);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.Version, right.Version);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.ComponentReference, right.ComponentReference);
        }
    }

    private sealed class NoticeDiagnosticComparer : IComparer<NoticeDiagnostic>
    {
        public static NoticeDiagnosticComparer Instance { get; } = new();

        public int Compare(NoticeDiagnostic? left, NoticeDiagnostic? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            int result = StringComparer.Ordinal.Compare(left.Code, right.Code);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.PackageUrl, right.PackageUrl);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.Source, right.Source);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.Message, right.Message);
        }
    }
}
