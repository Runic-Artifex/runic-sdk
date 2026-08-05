using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RunicToolkit.MVVM.Protocol.Tests;

internal sealed class JsonSchemaEvaluator : IDisposable
{
    private readonly string schemaRoot;
    private readonly Dictionary<string, JsonDocument> documents = new(StringComparer.OrdinalIgnoreCase);

    public JsonSchemaEvaluator(string schemaRoot)
    {
        this.schemaRoot = Path.GetFullPath(schemaRoot);
    }

    public ValidationResult Validate(JsonElement instance, string schemaFile)
    {
        string fullPath = ResolveSchemaPath(schemaFile, this.schemaRoot);
        JsonElement schema = Load(fullPath).RootElement;
        List<string> errors = [];
        Validate(instance, schema, fullPath, "$", errors);
        return new ValidationResult(errors);
    }

    public void Dispose()
    {
        foreach (JsonDocument document in this.documents.Values)
        {
            document.Dispose();
        }
    }

    private void Validate(JsonElement instance, JsonElement schema, string schemaFile, string instancePath, List<string> errors)
    {
        if (schema.ValueKind == JsonValueKind.True)
        {
            return;
        }

        if (schema.ValueKind == JsonValueKind.False)
        {
            errors.Add($"{instancePath}: false schema rejects every value.");
            return;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{instancePath}: schema is neither an object nor a boolean.");
            return;
        }

        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            (JsonElement target, string targetFile) = ResolveReference(reference.GetString()!, schemaFile);
            Validate(instance, target, targetFile, instancePath, errors);
        }

        ValidateCombinators(instance, schema, schemaFile, instancePath, errors);

        if (schema.TryGetProperty("type", out JsonElement type) && !MatchesType(instance, type))
        {
            errors.Add($"{instancePath}: value does not have required type {type.GetRawText()}.");
            return;
        }

        if (schema.TryGetProperty("const", out JsonElement constant) && !JsonEquals(instance, constant))
        {
            errors.Add($"{instancePath}: value does not equal const {constant.GetRawText()}.");
        }

        if (schema.TryGetProperty("enum", out JsonElement enumeration)
            && !enumeration.EnumerateArray().Any(value => JsonEquals(instance, value)))
        {
            errors.Add($"{instancePath}: value is not in enum {enumeration.GetRawText()}.");
        }

        switch (instance.ValueKind)
        {
            case JsonValueKind.Object:
                ValidateObject(instance, schema, schemaFile, instancePath, errors);
                break;
            case JsonValueKind.Array:
                ValidateArray(instance, schema, schemaFile, instancePath, errors);
                break;
            case JsonValueKind.String:
                ValidateString(instance.GetString()!, schema, instancePath, errors);
                break;
            case JsonValueKind.Number:
                ValidateNumber(instance, schema, instancePath, errors);
                break;
        }
    }

    private void ValidateCombinators(JsonElement instance, JsonElement schema, string schemaFile, string path, List<string> errors)
    {
        if (schema.TryGetProperty("allOf", out JsonElement allOf))
        {
            foreach (JsonElement child in allOf.EnumerateArray())
            {
                Validate(instance, child, schemaFile, path, errors);
            }
        }

        if (schema.TryGetProperty("anyOf", out JsonElement anyOf))
        {
            int matches = CountMatches(instance, anyOf, schemaFile, path);
            if (matches == 0)
            {
                errors.Add($"{path}: value does not match any anyOf branch.");
            }
        }

        if (schema.TryGetProperty("oneOf", out JsonElement oneOf))
        {
            int matches = CountMatches(instance, oneOf, schemaFile, path);
            if (matches != 1)
            {
                errors.Add($"{path}: value matches {matches} oneOf branches; exactly one is required.");
            }
        }

        if (schema.TryGetProperty("not", out JsonElement notSchema) && IsMatch(instance, notSchema, schemaFile, path))
        {
            errors.Add($"{path}: value matches a forbidden not schema.");
        }

        if (schema.TryGetProperty("if", out JsonElement ifSchema))
        {
            bool condition = IsMatch(instance, ifSchema, schemaFile, path);
            string keyword = condition ? "then" : "else";
            if (schema.TryGetProperty(keyword, out JsonElement branch))
            {
                Validate(instance, branch, schemaFile, path, errors);
            }
        }
    }

    private int CountMatches(JsonElement instance, JsonElement schemas, string schemaFile, string path)
    {
        int matches = 0;
        foreach (JsonElement child in schemas.EnumerateArray())
        {
            if (IsMatch(instance, child, schemaFile, path))
            {
                matches++;
            }
        }

        return matches;
    }

    private bool IsMatch(JsonElement instance, JsonElement schema, string schemaFile, string path)
    {
        List<string> branchErrors = [];
        Validate(instance, schema, schemaFile, path, branchErrors);
        return branchErrors.Count == 0;
    }

    private void ValidateObject(JsonElement instance, JsonElement schema, string schemaFile, string path, List<string> errors)
    {
        JsonProperty[] instanceProperties = instance.EnumerateObject().ToArray();
        ValidateCount(schema, "minProperties", "maxProperties", instanceProperties.Length, path, "properties", errors);

        if (schema.TryGetProperty("required", out JsonElement required))
        {
            foreach (JsonElement name in required.EnumerateArray())
            {
                if (!instance.TryGetProperty(name.GetString()!, out _))
                {
                    errors.Add($"{path}: required property '{name.GetString()}' is missing.");
                }
            }
        }

        schema.TryGetProperty("properties", out JsonElement declaredProperties);
        bool hasDeclaredProperties = declaredProperties.ValueKind == JsonValueKind.Object;

        foreach (JsonProperty property in instanceProperties)
        {
            string propertyPath = path + "." + property.Name;
            JsonElement propertySchema = default;
            bool declared = hasDeclaredProperties && declaredProperties.TryGetProperty(property.Name, out propertySchema);
            if (declared)
            {
                Validate(property.Value, propertySchema, schemaFile, propertyPath, errors);
            }
            else if (schema.TryGetProperty("additionalProperties", out JsonElement additional))
            {
                if (additional.ValueKind == JsonValueKind.False)
                {
                    errors.Add($"{path}: additional property '{property.Name}' is forbidden.");
                }
                else if (additional.ValueKind is JsonValueKind.Object or JsonValueKind.True)
                {
                    Validate(property.Value, additional, schemaFile, propertyPath, errors);
                }
            }

            if (schema.TryGetProperty("propertyNames", out JsonElement propertyNames))
            {
                using JsonDocument nameDocument = JsonDocument.Parse(JsonSerializer.Serialize(property.Name));
                Validate(nameDocument.RootElement, propertyNames, schemaFile, propertyPath + " (name)", errors);
            }
        }
    }

    private void ValidateArray(JsonElement instance, JsonElement schema, string schemaFile, string path, List<string> errors)
    {
        JsonElement.ArrayEnumerator valuesEnumerator = instance.EnumerateArray();
        JsonElement[] values = valuesEnumerator.ToArray();
        ValidateCount(schema, "minItems", "maxItems", values.Length, path, "items", errors);

        if (schema.TryGetProperty("uniqueItems", out JsonElement unique) && unique.ValueKind == JsonValueKind.True)
        {
            for (int left = 0; left < values.Length; left++)
            {
                for (int right = left + 1; right < values.Length; right++)
                {
                    if (JsonEquals(values[left], values[right]))
                    {
                        errors.Add($"{path}: array items {left} and {right} are not unique.");
                    }
                }
            }
        }

        if (schema.TryGetProperty("items", out JsonElement itemSchema))
        {
            for (int index = 0; index < values.Length; index++)
            {
                Validate(values[index], itemSchema, schemaFile, $"{path}[{index}]", errors);
            }
        }
    }

    private static void ValidateString(string value, JsonElement schema, string path, List<string> errors)
    {
        int length = value.EnumerateRunes().Count();
        ValidateCount(schema, "minLength", "maxLength", length, path, "Unicode scalar values", errors);

        if (schema.TryGetProperty("pattern", out JsonElement pattern)
            && !Regex.IsMatch(value, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            errors.Add($"{path}: string does not match pattern '{pattern.GetString()}'.");
        }
    }

    private static void ValidateNumber(JsonElement instance, JsonElement schema, string path, List<string> errors)
    {
        decimal value;
        try
        {
            value = instance.GetDecimal();
        }
        catch (FormatException)
        {
            double floatingValue = instance.GetDouble();
            if (double.IsNaN(floatingValue) || double.IsInfinity(floatingValue))
            {
                errors.Add($"{path}: number is not finite.");
            }

            return;
        }

        CheckBound(schema, "minimum", value, inclusive: true, lower: true, path, errors);
        CheckBound(schema, "maximum", value, inclusive: true, lower: false, path, errors);
        CheckBound(schema, "exclusiveMinimum", value, inclusive: false, lower: true, path, errors);
        CheckBound(schema, "exclusiveMaximum", value, inclusive: false, lower: false, path, errors);

        if (schema.TryGetProperty("multipleOf", out JsonElement multipleOf))
        {
            decimal divisor = multipleOf.GetDecimal();
            if (divisor <= 0 || value % divisor != 0)
            {
                errors.Add($"{path}: {value.ToString(CultureInfo.InvariantCulture)} is not a multiple of {divisor.ToString(CultureInfo.InvariantCulture)}.");
            }
        }
    }

    private static void CheckBound(JsonElement schema, string keyword, decimal value, bool inclusive, bool lower, string path, List<string> errors)
    {
        if (!schema.TryGetProperty(keyword, out JsonElement boundElement))
        {
            return;
        }

        decimal bound = boundElement.GetDecimal();
        bool valid = lower
            ? inclusive ? value >= bound : value > bound
            : inclusive ? value <= bound : value < bound;
        if (!valid)
        {
            errors.Add($"{path}: {value.ToString(CultureInfo.InvariantCulture)} violates {keyword} {bound.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private static void ValidateCount(JsonElement schema, string minimumName, string maximumName, int count, string path, string unit, List<string> errors)
    {
        if (schema.TryGetProperty(minimumName, out JsonElement minimum) && count < minimum.GetInt32())
        {
            errors.Add($"{path}: {count} {unit} is less than {minimumName} {minimum.GetInt32()}.");
        }

        if (schema.TryGetProperty(maximumName, out JsonElement maximum) && count > maximum.GetInt32())
        {
            errors.Add($"{path}: {count} {unit} exceeds {maximumName} {maximum.GetInt32()}.");
        }
    }

    private static bool MatchesType(JsonElement instance, JsonElement type)
    {
        if (type.ValueKind == JsonValueKind.Array)
        {
            return type.EnumerateArray().Any(item => MatchesType(instance, item));
        }

        return type.GetString() switch
        {
            "null" => instance.ValueKind == JsonValueKind.Null,
            "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "object" => instance.ValueKind == JsonValueKind.Object,
            "array" => instance.ValueKind == JsonValueKind.Array,
            "number" => instance.ValueKind == JsonValueKind.Number,
            "integer" => IsInteger(instance),
            "string" => instance.ValueKind == JsonValueKind.String,
            _ => false,
        };
    }

    private static bool IsInteger(JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        try
        {
            decimal value = instance.GetDecimal();
            return decimal.Truncate(value) == value;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
        {
            try
            {
                return left.GetDecimal() == right.GetDecimal();
            }
            catch (FormatException)
            {
                return left.GetDouble().Equals(right.GetDouble());
            }
        }

        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        if (left.ValueKind == JsonValueKind.Object)
        {
            JsonProperty[] leftProperties = left.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
            JsonProperty[] rightProperties = right.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
            return leftProperties.Length == rightProperties.Length
                && leftProperties.Zip(rightProperties).All(pair => pair.First.Name == pair.Second.Name && JsonEquals(pair.First.Value, pair.Second.Value));
        }

        if (left.ValueKind == JsonValueKind.Array)
        {
            JsonElement[] leftItems = left.EnumerateArray().ToArray();
            JsonElement[] rightItems = right.EnumerateArray().ToArray();
            return leftItems.Length == rightItems.Length && leftItems.Zip(rightItems).All(pair => JsonEquals(pair.First, pair.Second));
        }

        return left.ValueKind == JsonValueKind.String
            ? string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal)
            : left.GetRawText() == right.GetRawText();
    }

    private (JsonElement Schema, string File) ResolveReference(string reference, string currentSchemaFile)
    {
        int hashIndex = reference.IndexOf('#', StringComparison.Ordinal);
        string filePart = hashIndex >= 0 ? reference[..hashIndex] : reference;
        string fragment = hashIndex >= 0 ? reference[(hashIndex + 1)..] : string.Empty;
        string targetFile = filePart.Length == 0
            ? currentSchemaFile
            : ResolveSchemaPath(filePart, Path.GetDirectoryName(currentSchemaFile)!);
        JsonElement target = Load(targetFile).RootElement;

        if (fragment.Length == 0)
        {
            return (target, targetFile);
        }

        if (!fragment.StartsWith('/'))
        {
            throw new InvalidDataException($"Unsupported non-pointer schema reference '{reference}'.");
        }

        foreach (string encodedSegment in fragment[1..].Split('/'))
        {
            string segment = Uri.UnescapeDataString(encodedSegment).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (target.ValueKind == JsonValueKind.Object)
            {
                target = target.GetProperty(segment);
            }
            else if (target.ValueKind == JsonValueKind.Array && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                target = target[index];
            }
            else
            {
                throw new InvalidDataException($"Schema reference '{reference}' does not resolve.");
            }
        }

        return (target, targetFile);
    }

    private JsonDocument Load(string path)
    {
        if (!this.documents.TryGetValue(path, out JsonDocument? document))
        {
            document = JsonDocument.Parse(File.ReadAllBytes(path));
            this.documents.Add(path, document);
        }

        return document;
    }

    private string ResolveSchemaPath(string relativePath, string baseDirectory)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Rooted schema path '{relativePath}' is forbidden.");
        }

        string fullPath = Path.GetFullPath(relativePath, baseDirectory);
        string rootWithSeparator = this.schemaRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        StringComparison pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootWithSeparator, pathComparison))
        {
            throw new InvalidDataException($"Schema path '{relativePath}' escapes the schema root.");
        }

        return fullPath;
    }
}

internal sealed record ValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => this.Errors.Count == 0;
}
