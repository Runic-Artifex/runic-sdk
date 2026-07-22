using System;
using System.Collections.Generic;
using System.Text.Json;

namespace WebUIToolkit.DependencyNotices.Sbom;

internal static class SpdxJsonReader
{
    public static SbomDocument Read(JsonElement root, int maximumComponents)
    {
        string documentElementReference = SbomReader.RequireString(root, "SPDXID");
        string? documentNamespace = SbomReader.OptionalString(root, "documentNamespace");
        string documentReference = documentNamespace ?? documentElementReference;
        List<SbomComponent> components = [];
        if (root.TryGetProperty("packages", out JsonElement packages))
        {
            SbomReader.RequireKind(packages, JsonValueKind.Array, "SPDX 'packages' must be an array.");
            foreach (JsonElement package in packages.EnumerateArray())
            {
                if (components.Count >= maximumComponents)
                {
                    throw new SbomFormatException($"The SBOM exceeds the {maximumComponents} component limit.");
                }

                SbomReader.RequireKind(package, JsonValueKind.Object, "Each SPDX package must be an object.");
                string reference = SbomReader.RequireString(package, "SPDXID");
                string name = SbomReader.RequireString(package, "name");
                string version = SbomReader.RequireString(package, "versionInfo");
                PackageUrl? purl = ReadPackageUrl(package, reference, out string? ecosystem);
                components.Add(new SbomComponent(reference, purl, purl?.Type ?? ecosystem, name, version));
            }
        }

        return new SbomDocument(SbomFormat.SpdxJson, documentReference, null, components.AsReadOnly());
    }

    private static PackageUrl? ReadPackageUrl(JsonElement package, string reference, out string? ecosystem)
    {
        ecosystem = null;
        if (!package.TryGetProperty("externalRefs", out JsonElement references))
        {
            return null;
        }

        SbomReader.RequireKind(references, JsonValueKind.Array, "SPDX 'externalRefs' must be an array.");
        PackageUrl? result = null;
        foreach (JsonElement externalReference in references.EnumerateArray())
        {
            SbomReader.RequireKind(externalReference, JsonValueKind.Object, "SPDX external references must be objects.");
            string? referenceType = SbomReader.OptionalString(externalReference, "referenceType");
            if (referenceType is null)
            {
                continue;
            }

            if (!StringComparer.OrdinalIgnoreCase.Equals(referenceType, "purl"))
            {
                string? category = SbomReader.OptionalString(externalReference, "referenceCategory");
                if (category is not null &&
                    StringComparer.OrdinalIgnoreCase.Equals(category, "PACKAGE-MANAGER") &&
                    (StringComparer.OrdinalIgnoreCase.Equals(referenceType, "npm") ||
                     StringComparer.OrdinalIgnoreCase.Equals(referenceType, "nuget")))
                {
                    string declared = referenceType.ToLowerInvariant();
                    if (ecosystem is not null && !StringComparer.Ordinal.Equals(ecosystem, declared))
                    {
                        throw new SbomFormatException($"SPDX package '{reference}' contains conflicting package-manager references.");
                    }

                    ecosystem = declared;
                }

                continue;
            }

            string locator = SbomReader.RequireString(externalReference, "referenceLocator");
            if (!PackageUrl.TryParse(locator, out PackageUrl? parsed))
            {
                throw new SbomFormatException($"SPDX package '{reference}' contains an invalid Package URL.");
            }

            if (result is not null && !StringComparer.Ordinal.Equals(result.CanonicalValue, parsed.CanonicalValue))
            {
                throw new SbomFormatException($"SPDX package '{reference}' contains conflicting Package URL references.");
            }

            result = parsed;
        }

        if (result is not null && ecosystem is not null && !StringComparer.Ordinal.Equals(result.Type, ecosystem))
        {
            throw new SbomFormatException($"SPDX package '{reference}' contains conflicting package-manager and Package URL ecosystems.");
        }

        return result;
    }
}
