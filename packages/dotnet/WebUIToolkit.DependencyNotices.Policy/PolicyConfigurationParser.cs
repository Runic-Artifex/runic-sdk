using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WebUIToolkit.DependencyNotices.Spdx;

namespace WebUIToolkit.DependencyNotices.Policy;

/// <summary>Strictly parses the version 1 dependency-notices policy contract.</summary>
public static class PolicyConfigurationParser
{
    private static readonly string[] RootProperties = ["schemaVersion", "defaultDecision", "licenses", "missingEvidence", "orExpressions", "overrides"];
    private static readonly string[] LicenseProperties = ["allow", "deny", "review", "obligations"];
    private static readonly string[] OverrideProperties = ["id", "purl", "set", "reason", "approvedBy", "createdOn", "expiresAfter"];
    private static readonly string[] SetProperties = ["licenseExpression", "licenseEvidenceSha256"];

    public static PolicyConfiguration Parse(string json, PolicyParserLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        PolicyParserLimits effectiveLimits = limits ?? new PolicyParserLimits();
        effectiveLimits.Validate();
        int byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount > effectiveLimits.MaximumUtf8Bytes)
        {
            throw Invalid("", $"Policy input is {byteCount} UTF-8 bytes; the limit is {effectiveLimits.MaximumUtf8Bytes}.");
        }

        return ParseCore(json, effectiveLimits);
    }

    public static PolicyConfiguration Parse(ReadOnlySpan<byte> utf8Json, PolicyParserLimits? limits = null)
    {
        PolicyParserLimits effectiveLimits = limits ?? new PolicyParserLimits();
        effectiveLimits.Validate();
        if (utf8Json.Length > effectiveLimits.MaximumUtf8Bytes)
        {
            throw Invalid("", $"Policy input is {utf8Json.Length} UTF-8 bytes; the limit is {effectiveLimits.MaximumUtf8Bytes}.");
        }

        try
        {
            return ParseCore(new UTF8Encoding(false, true).GetString(utf8Json), effectiveLimits);
        }
        catch (DecoderFallbackException exception)
        {
            throw new PolicyConfigurationException(PolicyDiagnosticCodes.InvalidConfiguration, "", "Policy input is not valid UTF-8.", exception);
        }
    }

    private static PolicyConfiguration ParseCore(string json, PolicyParserLimits limits)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = limits.MaximumDepth,
            });
            int values = 0;
            ValidateTree(document.RootElement, "", limits, ref values);
            return ReadPolicy(document.RootElement);
        }
        catch (PolicyConfigurationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new PolicyConfigurationException(
                PolicyDiagnosticCodes.InvalidConfiguration,
                "",
                $"Policy JSON is invalid at byte {exception.BytePositionInLine}.",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            // System.Text.Json defers decoding escaped property names and string values.
            // GetString/JsonProperty.Name can therefore reject an unpaired surrogate only
            // after the document was parsed. Keep that implementation detail behind the
            // policy parser's stable domain boundary.
            throw new PolicyConfigurationException(
                PolicyDiagnosticCodes.InvalidConfiguration,
                "",
                "Policy JSON contains an invalid escaped Unicode string.",
                exception);
        }
    }

    private static PolicyConfiguration ReadPolicy(JsonElement root)
    {
        RequireKind(root, JsonValueKind.Object, "");
        RejectUnknownProperties(root, RootProperties, "");
        int schemaVersion = ReadRequiredInt(root, "schemaVersion", "");
        if (schemaVersion != 1)
        {
            throw new PolicyConfigurationException(
                PolicyDiagnosticCodes.UnsupportedSchemaVersion,
                "/schemaVersion",
                $"Policy schema version {schemaVersion} is unsupported; expected 1.");
        }

        PolicyDecision defaultDecision = ReadDecision(ReadRequiredString(root, "defaultDecision", ""), "/defaultDecision");
        JsonElement licensesElement = ReadRequired(root, "licenses", "");
        LicenseRuleSet licenses = ReadLicenses(licensesElement);
        PolicyDiagnosticLevel missingEvidence = ReadDiagnosticLevel(ReadRequiredString(root, "missingEvidence", ""), "/missingEvidence");
        OrExpressionPolicy orExpressions = ReadOrPolicy(ReadRequiredString(root, "orExpressions", ""), "/orExpressions");
        IReadOnlyList<PolicyOverride> overrides = root.TryGetProperty("overrides", out JsonElement overridesElement)
            ? ReadOverrides(overridesElement)
            : Array.Empty<PolicyOverride>();
        return new PolicyConfiguration(schemaVersion, defaultDecision, licenses, missingEvidence, orExpressions, overrides);
    }

    private static LicenseRuleSet ReadLicenses(JsonElement element)
    {
        const string pointer = "/licenses";
        RequireKind(element, JsonValueKind.Object, pointer);
        RejectUnknownProperties(element, LicenseProperties, pointer);
        ReadOnlyCollection<string> allow = ReadRules(ReadRequired(element, "allow", pointer), pointer + "/allow");
        ReadOnlyCollection<string> deny = ReadRules(ReadRequired(element, "deny", pointer), pointer + "/deny");
        ReadOnlyCollection<string> review = ReadRules(ReadRequired(element, "review", pointer), pointer + "/review");
        Dictionary<string, IReadOnlyList<string>> obligations = new(StringComparer.Ordinal);
        if (element.TryGetProperty("obligations", out JsonElement obligationsElement))
        {
            RequireKind(obligationsElement, JsonValueKind.Object, pointer + "/obligations");
            foreach (JsonProperty property in obligationsElement.EnumerateObject())
            {
                ValidateRule(property.Name, pointer + "/obligations/" + Escape(property.Name));
                obligations.Add(property.Name, ReadNonEmptyUniqueStrings(property.Value, pointer + "/obligations/" + Escape(property.Name)));
            }
        }

        return new LicenseRuleSet(allow, deny, review, new ReadOnlyDictionary<string, IReadOnlyList<string>>(obligations));
    }

    private static ReadOnlyCollection<PolicyOverride> ReadOverrides(JsonElement element)
    {
        const string pointer = "/overrides";
        RequireKind(element, JsonValueKind.Array, pointer);
        List<PolicyOverride> result = [];
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            string itemPointer = pointer + "/" + index.ToString(CultureInfo.InvariantCulture);
            RequireKind(item, JsonValueKind.Object, itemPointer);
            RejectUnknownProperties(item, OverrideProperties, itemPointer);
            string id = ReadRequiredNonEmptyString(item, "id", itemPointer);
            string rawPurl = ReadRequiredNonEmptyString(item, "purl", itemPointer);
            if (!PackageUrl.TryParse(rawPurl, out PackageUrl? packageUrl) || !StringComparer.Ordinal.Equals(rawPurl, packageUrl.CanonicalValue))
            {
                throw Invalid(itemPointer + "/purl", "An override must target an exact canonical Package URL.");
            }

            JsonElement setElement = ReadRequired(item, "set", itemPointer);
            RequireKind(setElement, JsonValueKind.Object, itemPointer + "/set");
            RejectUnknownProperties(setElement, SetProperties, itemPointer + "/set");
            string expression = ReadRequiredNonEmptyString(setElement, "licenseExpression", itemPointer + "/set");
            ValidateSpdx(expression, itemPointer + "/set/licenseExpression");
            string digest = ReadRequiredNonEmptyString(setElement, "licenseEvidenceSha256", itemPointer + "/set");
            if (!IsLowerSha256(digest))
            {
                throw Invalid(itemPointer + "/set/licenseEvidenceSha256", "Override evidence must be a lowercase SHA-256 digest.");
            }

            string reason = ReadRequiredNonEmptyString(item, "reason", itemPointer);
            string approvedBy = ReadRequiredNonEmptyString(item, "approvedBy", itemPointer);
            string rawCreatedOn = ReadRequiredNonEmptyString(item, "createdOn", itemPointer);
            if (!DateOnly.TryParseExact(rawCreatedOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly createdOn))
            {
                throw Invalid(itemPointer + "/createdOn", "createdOn must be an ISO 8601 calendar date in yyyy-MM-dd form.");
            }

            string expiresAfter = ReadRequiredNonEmptyString(item, "expiresAfter", itemPointer);
            result.Add(new PolicyOverride(id, packageUrl, new PolicyOverrideSet(expression, digest), reason, approvedBy, createdOn, expiresAfter));
            index++;
        }

        return result.AsReadOnly();
    }

    private static ReadOnlyCollection<string> ReadRules(JsonElement element, string pointer)
    {
        ReadOnlyCollection<string> values = ReadNonEmptyUniqueStrings(element, pointer);
        for (int index = 0; index < values.Count; index++)
        {
            ValidateRule(values[index], pointer + "/" + index.ToString(CultureInfo.InvariantCulture));
        }

        return values;
    }

    private static ReadOnlyCollection<string> ReadNonEmptyUniqueStrings(JsonElement element, string pointer)
    {
        RequireKind(element, JsonValueKind.Array, pointer);
        List<string> values = [];
        HashSet<string> unique = new(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            string valuePointer = pointer + "/" + index.ToString(CultureInfo.InvariantCulture);
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw Invalid(valuePointer, "Expected a non-empty string.");
            }

            string value = item.GetString()!;
            if (!unique.Add(value))
            {
                throw Invalid(valuePointer, $"Duplicate value '{value}' is not permitted.");
            }

            values.Add(value);
            index++;
        }

        return values.AsReadOnly();
    }

    private static void ValidateRule(string rule, string pointer)
    {
        int wildcard = rule.IndexOf('*');
        if (wildcard >= 0)
        {
            if (wildcard != rule.Length - 1 || rule.IndexOf('*', wildcard + 1) >= 0 || wildcard == 0)
            {
                throw Invalid(pointer, "A wildcard rule must have one terminal '*' after a non-empty prefix.");
            }

            return;
        }

        ValidateSpdx(rule, pointer);
    }

    private static void ValidateSpdx(string expression, string pointer)
    {
        try
        {
            _ = SpdxParser.Parse(expression);
        }
        catch (SpdxParseException exception)
        {
            throw new PolicyConfigurationException(
                PolicyDiagnosticCodes.InvalidSpdxExpression,
                pointer,
                $"Invalid SPDX expression at offset {exception.Offset}; expected {exception.Expected}.",
                exception);
        }
    }

    private static void ValidateTree(JsonElement element, string pointer, PolicyParserLimits limits, ref int values)
    {
        values++;
        if (values > limits.MaximumValues)
        {
            throw Invalid(pointer, $"Policy contains more than {limits.MaximumValues} JSON values.");
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string propertyPointer = pointer + "/" + Escape(property.Name);
                if (!names.Add(property.Name))
                {
                    throw Invalid(propertyPointer, $"Duplicate property '{property.Name}' is not permitted.");
                }

                ValidateTree(property.Value, propertyPointer, limits, ref values);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                ValidateTree(item, pointer + "/" + index.ToString(CultureInfo.InvariantCulture), limits, ref values);
                index++;
            }
        }
    }

    private static void RejectUnknownProperties(JsonElement element, string[] allowed, string pointer)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (Array.IndexOf(allowed, property.Name) < 0)
            {
                throw Invalid(pointer + "/" + Escape(property.Name), $"Unknown property '{property.Name}' is not permitted by policy schema version 1.");
            }
        }
    }

    private static JsonElement ReadRequired(JsonElement element, string name, string pointer)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            throw Invalid(pointer, $"Required property '{name}' is missing.");
        }

        return value;
    }

    private static string ReadRequiredString(JsonElement element, string name, string pointer)
    {
        JsonElement value = ReadRequired(element, name, pointer);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid(pointer + "/" + name, "Expected a string.");
        }

        return value.GetString()!;
    }

    private static string ReadRequiredNonEmptyString(JsonElement element, string name, string pointer)
    {
        string value = ReadRequiredString(element, name, pointer);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid(pointer + "/" + name, "Expected a non-empty string.");
        }

        return value;
    }

    private static int ReadRequiredInt(JsonElement element, string name, string pointer)
    {
        JsonElement value = ReadRequired(element, name, pointer);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
        {
            throw Invalid(pointer + "/" + name, "Expected a 32-bit integer.");
        }

        return result;
    }

    private static PolicyDecision ReadDecision(string value, string pointer) => value switch
    {
        "allow" => PolicyDecision.Allow,
        "review" => PolicyDecision.Review,
        "deny" => PolicyDecision.Deny,
        _ => throw Invalid(pointer, "Expected 'allow', 'review', or 'deny'."),
    };

    private static PolicyDiagnosticLevel ReadDiagnosticLevel(string value, string pointer) => value switch
    {
        "warning" => PolicyDiagnosticLevel.Warning,
        "error" => PolicyDiagnosticLevel.Error,
        _ => throw Invalid(pointer, "Expected 'warning' or 'error'."),
    };

    private static OrExpressionPolicy ReadOrPolicy(string value, string pointer) => value switch
    {
        "allow" => OrExpressionPolicy.Allow,
        "require-explicit-selection" => OrExpressionPolicy.RequireExplicitSelection,
        _ => throw Invalid(pointer, "Expected 'allow' or 'require-explicit-selection'."),
    };

    private static void RequireKind(JsonElement element, JsonValueKind kind, string pointer)
    {
        if (element.ValueKind != kind)
        {
            throw Invalid(pointer, $"Expected JSON {kind.ToString().ToLowerInvariant()}.");
        }
    }

    private static bool IsLowerSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiHexDigitLower(character) && !char.IsDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static string Escape(string segment) => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static PolicyConfigurationException Invalid(string pointer, string message) =>
        new(PolicyDiagnosticCodes.InvalidConfiguration, pointer, message);
}
