using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;

namespace WebUIToolkit.DependencyNotices.Sbom;

public static class SbomReader
{
    public static SbomDocument Read(Stream stream, SbomReadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        limits ??= new SbomReadLimits();
        limits.Validate();

        byte[] bytes = ReadBounded(stream, limits.MaximumBytes);
        try
        {
            using JsonDocument json = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = limits.MaximumDepth,
            });

            ValidateTree(json.RootElement, limits.MaximumProperties);
            return ReadDocument(json.RootElement, limits.MaximumComponents);
        }
        catch (JsonException exception)
        {
            throw new SbomFormatException("The SBOM is not valid constrained JSON.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new SbomFormatException("The SBOM contains a JSON string that cannot be decoded as Unicode.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new SbomFormatException("The SBOM contains a JSON string that cannot be decoded as Unicode.", exception);
        }
    }

    public static SbomDocument Read(ReadOnlySpan<byte> utf8Json, SbomReadLimits? limits = null)
    {
        limits ??= new SbomReadLimits();
        limits.Validate();
        if (utf8Json.Length > limits.MaximumBytes)
        {
            throw new SbomFormatException($"The SBOM exceeds the {limits.MaximumBytes} byte limit.");
        }

        using MemoryStream stream = new(utf8Json.ToArray(), writable: false);
        return Read(stream, limits);
    }

    private static byte[] ReadBounded(Stream stream, long maximumBytes)
    {
        using MemoryStream buffer = new();
        byte[] rented = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            long total = 0;
            while (true)
            {
                int read = stream.Read(rented, 0, rented.Length);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maximumBytes)
                {
                    throw new SbomFormatException($"The SBOM exceeds the {maximumBytes} byte limit.");
                }

                buffer.Write(rented, 0, read);
            }

            return buffer.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void ValidateTree(JsonElement root, int maximumProperties)
    {
        int propertyCount = 0;
        Stack<JsonElement> pending = new();
        pending.Push(root);
        while (pending.Count != 0)
        {
            JsonElement current = pending.Pop();
            if (current.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (JsonProperty property in current.EnumerateObject())
                {
                    propertyCount++;
                    if (propertyCount > maximumProperties)
                    {
                        throw new SbomFormatException($"The SBOM exceeds the {maximumProperties} property limit.");
                    }

                    if (!names.Add(property.Name))
                    {
                        throw new SbomFormatException($"The SBOM contains duplicate property '{property.Name}'.");
                    }

                    pending.Push(property.Value);
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in current.EnumerateArray())
                {
                    pending.Push(item);
                }
            }
        }
    }

    private static SbomDocument ReadDocument(JsonElement root, int maximumComponents)
    {
        RequireKind(root, JsonValueKind.Object, "The SBOM root must be an object.");
        if (TryString(root, "bomFormat", out string? bomFormat) &&
            StringComparer.OrdinalIgnoreCase.Equals(bomFormat, "CycloneDX"))
        {
            return CycloneDxReader.Read(root, maximumComponents);
        }

        if (TryString(root, "spdxVersion", out string? spdxVersion) &&
            spdxVersion.StartsWith("SPDX-", StringComparison.OrdinalIgnoreCase))
        {
            return SpdxJsonReader.Read(root, maximumComponents);
        }

        throw new SbomFormatException("The JSON document is neither the supported CycloneDX nor SPDX subset.");
    }

    internal static string RequireString(JsonElement element, string propertyName)
    {
        if (!TryString(element, propertyName, out string? value) || value.Length == 0)
        {
            throw new SbomFormatException($"Required non-empty string property '{propertyName}' is missing.");
        }

        return value;
    }

    internal static string? OptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String || property.GetString() is not string value || value.Length == 0)
        {
            throw new SbomFormatException($"Optional property '{propertyName}' must be a non-empty string when present.");
        }

        return value;
    }

    internal static bool TryString(
        JsonElement element,
        string propertyName,
        [NotNullWhen(true)] out string? value)
    {
        if (element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return value is not null;
        }

        value = null;
        return false;
    }

    internal static void RequireKind(JsonElement element, JsonValueKind kind, string message)
    {
        if (element.ValueKind != kind)
        {
            throw new SbomFormatException(message);
        }
    }
}
