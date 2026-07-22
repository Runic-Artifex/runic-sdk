using System;
using System.Collections.Generic;
using System.Text.Json;

namespace WebUIToolkit.DependencyNotices.Sbom;

internal static class CycloneDxReader
{
    public static SbomDocument Read(JsonElement root, int maximumComponents)
    {
        string? serialNumber = SbomReader.OptionalString(root, "serialNumber");
        string documentReference = serialNumber ?? "CycloneDX";
        List<SbomComponent> components = [];
        if (root.TryGetProperty("components", out JsonElement componentArray))
        {
            SbomReader.RequireKind(componentArray, JsonValueKind.Array, "CycloneDX 'components' must be an array.");
            foreach (JsonElement component in componentArray.EnumerateArray())
            {
                ReadComponent(component, components, maximumComponents);
            }
        }

        return new SbomDocument(SbomFormat.CycloneDxJson, documentReference, serialNumber, components.AsReadOnly());
    }

    private static void ReadComponent(JsonElement element, List<SbomComponent> result, int maximumComponents)
    {
        SbomReader.RequireKind(element, JsonValueKind.Object, "Each CycloneDX component must be an object.");
        if (result.Count >= maximumComponents)
        {
            throw new SbomFormatException($"The SBOM exceeds the {maximumComponents} component limit.");
        }

        string name = SbomReader.RequireString(element, "name");
        string version = SbomReader.RequireString(element, "version");
        string? componentReference = SbomReader.OptionalString(element, "bom-ref");
        string? purlText = SbomReader.OptionalString(element, "purl");
        PackageUrl? purl = ParsePackageUrl(purlText, componentReference ?? name);
        string? ecosystem = purl?.Type ?? ReadEcosystemProperty(element);
        result.Add(new SbomComponent(componentReference ?? purl?.CanonicalValue ?? $"{name}@{version}", purl, ecosystem, name, version));

        if (element.TryGetProperty("components", out JsonElement nested))
        {
            SbomReader.RequireKind(nested, JsonValueKind.Array, "Nested CycloneDX 'components' must be an array.");
            foreach (JsonElement child in nested.EnumerateArray())
            {
                ReadComponent(child, result, maximumComponents);
            }
        }
    }

    private static PackageUrl? ParsePackageUrl(string? value, string reference)
    {
        if (value is null)
        {
            return null;
        }

        if (!PackageUrl.TryParse(value, out PackageUrl? purl))
        {
            throw new SbomFormatException($"CycloneDX component '{reference}' contains an invalid Package URL.");
        }

        return purl;
    }

    private static string? ReadEcosystemProperty(JsonElement component)
    {
        if (!component.TryGetProperty("properties", out JsonElement properties))
        {
            return null;
        }

        SbomReader.RequireKind(properties, JsonValueKind.Array, "CycloneDX component 'properties' must be an array.");
        string? ecosystem = null;
        foreach (JsonElement property in properties.EnumerateArray())
        {
            SbomReader.RequireKind(property, JsonValueKind.Object, "CycloneDX component properties must be objects.");
            string name = SbomReader.RequireString(property, "name");
            if (!(StringComparer.OrdinalIgnoreCase.Equals(name, "ecosystem") ||
                  StringComparer.OrdinalIgnoreCase.Equals(name, "webuitoolkit:ecosystem")))
            {
                continue;
            }

            string value = SbomReader.RequireString(property, "value").ToLowerInvariant();
            if (ecosystem is not null && !StringComparer.Ordinal.Equals(ecosystem, value))
            {
                throw new SbomFormatException("A CycloneDX component declares conflicting ecosystem properties.");
            }

            ecosystem = value;
        }

        return ecosystem;
    }
}
