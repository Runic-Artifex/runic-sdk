using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WebUIToolkit.MVVM.Protocol.Tests;

internal static class Program
{
    private const string ProtocolIdentity = "webuitoolkit.mvvm/1";
    private const string SchemaDraft = "https://json-schema.org/draft/2020-12/schema";
    private static readonly string[] ExpectedFaultCodes =
    [
        "protocol.unsupported",
        "request.invalid",
        "member.unknown",
        "revision.stale",
        "limit.exceeded",
        "request.cancelled",
        "request.timeout",
        "session.closed",
    ];
    private static readonly string[] ExpectedCancellationCaseIds = ["cancel-wins", "completion-wins", "timeout-wins"];
    private static readonly string[] ExpectedSemanticCaseIds =
    [
        "cancellation-and-timeout",
        "culture-and-identities",
        "fault-catalog",
        "limits",
        "reconnect-snapshot",
        "sanitization",
        "stale-rejection",
        "successful-mutation",
    ];
    private static readonly KeyValuePair<string, int>[] ExpectedHardCeilings =
    [
        KeyValuePair.Create("maxFrameBytes", 1048576),
        KeyValuePair.Create("maxJsonDepth", 32),
        KeyValuePair.Create("maxGeneralStringUtf8Bytes", 65536),
        KeyValuePair.Create("maxObjectProperties", 4096),
        KeyValuePair.Create("maxObjectPropertyNameUtf8Bytes", 128),
        KeyValuePair.Create("maxArrayItems", 10000),
        KeyValuePair.Create("maxContractUtf8Bytes", 128),
        KeyValuePair.Create("capabilityTokenCharacters", 43),
        KeyValuePair.Create("maxSanitizedMessageUtf8Bytes", 256),
        KeyValuePair.Create("maxSessions", 16),
        KeyValuePair.Create("maxPendingRequestsPerSession", 64),
        KeyValuePair.Create("maxSnapshotMembers", 4096),
        KeyValuePair.Create("maxPatchChanges", 1024),
        KeyValuePair.Create("maxCollectionItems", 10000),
        KeyValuePair.Create("maxInsertedOrReplacedItemsPerPatch", 10000),
        KeyValuePair.Create("maxCommandTimeoutMilliseconds", 300000),
    ];

    public static int Main()
    {
        try
        {
            string protocolRoot = Path.Combine(AppContext.BaseDirectory, "Protocol");
            string schemaRoot = Path.Combine(protocolRoot, "schema", "v1");
            string corpusRoot = Path.Combine(protocolRoot, "corpus", "v1");

            RunContractMetadataTests(schemaRoot, corpusRoot);
            RunCorpusTests(schemaRoot, corpusRoot);
            RunSemanticTests(corpusRoot);

            Console.WriteLine("PASS: WebUIToolkit MVVM protocol v1 schema, corpus, and semantic invariants.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception.Message);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void RunContractMetadataTests(string schemaRoot, string corpusRoot)
    {
        string[] schemaFiles =
        [
            Path.Combine(schemaRoot, "common.schema.json"),
            Path.Combine(schemaRoot, "client-message.schema.json"),
            Path.Combine(schemaRoot, "host-message.schema.json"),
        ];

        foreach (string schemaFile in schemaFiles)
        {
            Assert(File.Exists(schemaFile), $"Schema file is missing: {schemaFile}");
            using JsonDocument schema = LoadDocument(schemaFile);
            Assert(schema.RootElement.GetProperty("$schema").GetString() == SchemaDraft, $"{schemaFile} uses the wrong schema draft.");
            Assert(!ContainsProperty(schema.RootElement, "$id"), $"{schemaFile} must omit $id until a schema domain is owned.");
        }

        using JsonDocument common = LoadDocument(schemaFiles[0]);
        string[] actualCodes = common.RootElement
            .GetProperty("$defs")
            .GetProperty("faultCode")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        Assert(actualCodes.SequenceEqual(ExpectedFaultCodes, StringComparer.Ordinal), "The schema must contain the exact ordered eight-code v1 fault catalog.");

        using JsonDocument manifest = LoadDocument(Path.Combine(corpusRoot, "manifest.json"));
        JsonElement root = manifest.RootElement;
        Assert(root.GetProperty("protocolIdentity").GetString() == ProtocolIdentity, "Manifest protocol identity must be webuitoolkit.mvvm/1.");
        Assert(root.GetProperty("schemaDraft").GetString() == SchemaDraft, "Manifest schema draft must be JSON Schema 2020-12.");

        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (JsonElement entry in root.GetProperty("cases").EnumerateArray().Concat(root.GetProperty("semanticCases").EnumerateArray()))
        {
            string id = RequiredString(entry, "id");
            Assert(ids.Add(id), $"Duplicate manifest ID '{id}'.");
            ResolveCorpusFile(corpusRoot, RequiredString(entry, "file"));
        }
    }

    private static void RunCorpusTests(string schemaRoot, string corpusRoot)
    {
        using JsonDocument manifest = LoadDocument(Path.Combine(corpusRoot, "manifest.json"));
        using JsonSchemaEvaluator evaluator = new(schemaRoot);
        int documentsValidated = 0;

        foreach (JsonElement entry in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            string id = RequiredString(entry, "id");
            string file = ResolveCorpusFile(corpusRoot, RequiredString(entry, "file"));
            string schemaName = RequiredString(entry, "schema");
            string schemaFile = schemaName switch
            {
                "client" => "client-message.schema.json",
                "host" => "host-message.schema.json",
                _ => throw new InvalidDataException($"Case '{id}' names unknown schema '{schemaName}'."),
            };
            bool expectedValid = entry.GetProperty("valid").GetBoolean();
            string documentMode = RequiredString(entry, "documentMode");
            using JsonDocument fixture = LoadDocument(file);
            IEnumerable<JsonElement> documents = documentMode switch
            {
                "single" => [fixture.RootElement],
                "eachItem" when fixture.RootElement.ValueKind == JsonValueKind.Array => fixture.RootElement.EnumerateArray().ToArray(),
                "eachItem" => throw new InvalidDataException($"Case '{id}' uses eachItem but its fixture is not an array."),
                _ => throw new InvalidDataException($"Case '{id}' uses unknown documentMode '{documentMode}'."),
            };

            int index = 0;
            foreach (JsonElement document in documents)
            {
                ValidationResult result = evaluator.Validate(document, schemaFile);
                if (result.IsValid != expectedValid)
                {
                    string errors = result.Errors.Count == 0 ? "no validation errors" : string.Join(Environment.NewLine, result.Errors);
                    throw new InvalidDataException($"Corpus case '{id}' document {index} expected valid={expectedValid} but was valid={result.IsValid}:{Environment.NewLine}{errors}");
                }

                documentsValidated++;
                index++;
            }

            Assert(index > 0, $"Corpus case '{id}' contains no documents.");
        }

        Console.WriteLine($"PASS: validated {documentsValidated} wire documents against their real schemas.");
    }

    private static void RunSemanticTests(string corpusRoot)
    {
        using JsonDocument manifest = LoadDocument(Path.Combine(corpusRoot, "manifest.json"));
        Dictionary<string, string> fixtures = manifest.RootElement.GetProperty("semanticCases").EnumerateArray()
            .ToDictionary(
                item => RequiredString(item, "id"),
                item => ResolveCorpusFile(corpusRoot, RequiredString(item, "file")),
                StringComparer.Ordinal);
        Assert(fixtures.Keys.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(ExpectedSemanticCaseIds, StringComparer.Ordinal), "Manifest must register the exact four v1 semantic cases.");

        ValidateSuccessfulMutation(fixtures["successful-mutation"]);
        ValidateStaleRejection(fixtures["stale-rejection"]);
        ValidateCancellationAndTimeout(fixtures["cancellation-and-timeout"]);
        ValidateReconnectSnapshot(fixtures["reconnect-snapshot"]);
        ValidateLimits(fixtures["limits"]);
        ValidateFaultCatalog(fixtures["fault-catalog"]);
        ValidateSanitization(fixtures["sanitization"]);
        ValidateCultureAndIdentities(fixtures["culture-and-identities"]);
        Console.WriteLine("PASS: validated all 8 registered semantic fixtures, including exact-once revision, cancellation, stale-write, and reconnect semantics.");
    }

    private static void ValidateSuccessfulMutation(string file)
    {
        using JsonDocument document = LoadDocument(file);
        JsonElement root = document.RootElement;
        long initial = root.GetProperty("initialRevision").GetInt64();
        JsonElement input = root.GetProperty("input");
        JsonElement[] outputs = root.GetProperty("outputs").EnumerateArray().ToArray();
        JsonElement expected = root.GetProperty("expected");

        Assert(input.GetProperty("kind").GetString() == "setProperty", "successful-mutation input must be setProperty.");
        Assert(input.GetProperty("baseRevision").GetInt64() == initial, "successful-mutation must start at its authoritative revision.");
        Assert(outputs.Length == 2, "successful-mutation must produce one patch and one terminal result.");
        Assert(outputs[0].GetProperty("kind").GetString() == "patch" && outputs[1].GetProperty("kind").GetString() == "result", "successful-mutation must publish its patch before its result.");
        long finalRevision = expected.GetProperty("finalRevision").GetInt64();
        JsonElement patch = outputs[0].GetProperty("payload");
        Assert(patch.GetProperty("fromRevision").GetInt64() == initial, "successful-mutation patch must start at initialRevision.");
        Assert(patch.GetProperty("toRevision").GetInt64() == finalRevision, "successful-mutation patch must end at finalRevision.");
        Assert(outputs[1].GetProperty("request").GetString() == input.GetProperty("request").GetString(), "successful-mutation result must correlate to its request.");
        Assert(outputs[1].GetProperty("payload").GetProperty("revision").GetInt64() == finalRevision, "successful-mutation result must report finalRevision.");
        Assert(expected.GetProperty("consumerInvocations").GetInt32() == 1, "successful-mutation invokes consumer code exactly once.");
        Assert(expected.GetProperty("revisionIncrements").GetInt32() == 1 && finalRevision == checked(initial + 1), "successful-mutation advances revision exactly once.");
        Assert(expected.GetProperty("terminalOutcomeCount").GetInt32() == 1, "successful-mutation has exactly one terminal outcome.");
    }

    private static void ValidateStaleRejection(string file)
    {
        using JsonDocument document = LoadDocument(file);
        JsonElement root = document.RootElement;
        long initial = root.GetProperty("initialRevision").GetInt64();
        JsonElement input = root.GetProperty("input");
        JsonElement[] outputs = root.GetProperty("outputs").EnumerateArray().ToArray();
        JsonElement expected = root.GetProperty("expected");

        Assert(input.GetProperty("baseRevision").GetInt64() < initial, "stale-rejection input must actually be stale.");
        Assert(outputs.Length == 1 && outputs[0].GetProperty("kind").GetString() == "fault", "stale-rejection must produce one fault only.");
        JsonElement fault = outputs[0].GetProperty("payload");
        Assert(fault.GetProperty("code").GetString() == "revision.stale", "stale-rejection must use revision.stale.");
        Assert(fault.GetProperty("currentRevision").GetInt64() == initial && fault.GetProperty("snapshotRequired").GetBoolean(), "stale-rejection must expose the authoritative revision and require recovery.");
        Assert(outputs[0].GetProperty("request").GetString() == input.GetProperty("request").GetString(), "stale-rejection fault must correlate to its request.");
        Assert(expected.GetProperty("consumerInvocations").GetInt32() == 0, "stale-rejection must not invoke consumer code.");
        Assert(expected.GetProperty("finalRevision").GetInt64() == initial && expected.GetProperty("revisionIncrements").GetInt32() == 0, "stale-rejection must not advance revision.");
        Assert(expected.GetProperty("terminalOutcomeCount").GetInt32() == 1, "stale-rejection has exactly one terminal outcome.");
    }

    private static void ValidateCancellationAndTimeout(string file)
    {
        using JsonDocument document = LoadDocument(file);
        Dictionary<string, JsonElement> cases = document.RootElement.GetProperty("cases").EnumerateArray()
            .ToDictionary(item => RequiredString(item, "id"), item => item, StringComparer.Ordinal);
        Assert(cases.Keys.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(ExpectedCancellationCaseIds, StringComparer.Ordinal), "Cancellation fixture must contain the exact three race outcomes.");

        foreach ((string id, JsonElement item) in cases)
        {
            string[] signals = item.GetProperty("signals").EnumerateArray().Select(signal => signal.GetString()!).ToArray();
            Assert(signals.Length >= 2 && signals.Distinct(StringComparer.Ordinal).Count() == signals.Length, $"{id} signals must be ordered and unique.");
            Assert(item.GetProperty("terminalOutcomeCount").GetInt32() == 1, $"{id} must have exactly one terminal target outcome.");

            string first = signals[0];
            string? targetFault = item.GetProperty("targetFault").ValueKind == JsonValueKind.Null ? null : item.GetProperty("targetFault").GetString();
            bool cancelAccepted = item.GetProperty("cancelAccepted").GetBoolean();
            int revisionIncrements = item.GetProperty("revisionIncrements").GetInt32();
            switch (first)
            {
                case "cancel":
                    Assert(targetFault == "request.cancelled" && cancelAccepted && revisionIncrements == 0, "cancel-wins must accept cancellation, fault once, and preserve revision.");
                    break;
                case "completion":
                    Assert(targetFault is null && !cancelAccepted && revisionIncrements == 1, "completion-wins must reject late cancellation and commit exactly once.");
                    break;
                case "timeout":
                    Assert(targetFault == "request.timeout" && !cancelAccepted && revisionIncrements == 0, "timeout-wins must reject late cancellation, fault once, and preserve revision.");
                    break;
                default:
                    throw new InvalidDataException($"{id} begins with unknown race signal '{first}'.");
            }
        }
    }

    private static void ValidateReconnectSnapshot(string file)
    {
        using JsonDocument document = LoadDocument(file);
        JsonElement root = document.RootElement;
        long clientRevision = root.GetProperty("clientRevision").GetInt64();
        long hostRevision = root.GetProperty("hostRevision").GetInt64();
        Assert(clientRevision < hostRevision, "reconnect fixture must model a client behind the host.");
        Assert(root.GetProperty("inputKind").GetString() == "requestSnapshot", "reconnect recovery must request a snapshot.");
        Assert(root.GetProperty("expectedOutputKind").GetString() == "snapshot", "reconnect recovery must return a snapshot.");
        Assert(root.GetProperty("expectedRevision").GetInt64() == hostRevision, "reconnect snapshot must carry the authoritative host revision.");
        Assert(root.GetProperty("replaceLocalState").GetBoolean(), "reconnect snapshot must replace local state atomically.");
        Assert(!root.GetProperty("patchReplayRequired").GetBoolean(), "v1 reconnect recovery must not depend on patch replay.");
    }

    private static void ValidateLimits(string file)
    {
        using JsonDocument document = LoadDocument(file);
        JsonElement root = document.RootElement;
        JsonProperty[] ceilings = root.GetProperty("hardCeilings").EnumerateObject().ToArray();
        Assert(ceilings.Length == ExpectedHardCeilings.Length, "limits must define the exact v1 hard-ceiling set.");
        foreach (KeyValuePair<string, int> expected in ExpectedHardCeilings)
        {
            Assert(root.GetProperty("hardCeilings").GetProperty(expected.Key).GetInt32() == expected.Value, $"limits.{expected.Key} must equal {expected.Value}.");
        }

        Assert(root.GetProperty("expectedAtCeiling").GetString() == "accepted", "Values exactly at each hard ceiling must be accepted.");
        JsonElement above = root.GetProperty("expectedAboveCeiling");
        Assert(above.GetProperty("code").GetString() == "limit.exceeded", "Values above a hard ceiling must fail with limit.exceeded.");
        Assert(above.GetProperty("consumerInvocations").GetInt32() == 0 && above.GetProperty("revisionIncrements").GetInt32() == 0, "Limit rejection must occur before consumer invocation and revision advancement.");
    }

    private static void ValidateFaultCatalog(string file)
    {
        using JsonDocument document = LoadDocument(file);
        JsonElement root = document.RootElement;
        JsonElement[] entries = root.GetProperty("codes").EnumerateArray().ToArray();
        Assert(entries.Select(entry => RequiredString(entry, "code")).SequenceEqual(ExpectedFaultCodes, StringComparer.Ordinal), "fault-catalog must contain the schema's exact ordered eight fault codes.");
        Assert(!root.GetProperty("unknownCodeAllowed").GetBoolean(), "Unknown v1 fault codes must be rejected.");

        foreach (JsonElement entry in entries)
        {
            string code = RequiredString(entry, "code");
            bool expectedRetryable = code is "revision.stale" or "limit.exceeded" or "request.timeout";
            bool expectedSnapshot = code == "revision.stale";
            Assert(entry.GetProperty("retryable").GetBoolean() == expectedRetryable, $"Fault '{code}' has the wrong retryable classification.");
            Assert(entry.GetProperty("snapshotRequired").GetBoolean() == expectedSnapshot, $"Fault '{code}' has the wrong snapshotRequired classification.");
        }
    }

    private static void ValidateSanitization(string file)
    {
        using JsonDocument document = LoadDocument(file);
        JsonElement[] cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert(cases.Length == 6, "sanitization must cover one safe message and five forbidden disclosure categories.");
        Assert(cases.Count(item => item.GetProperty("allowed").GetBoolean()) == 1, "Only the implementation-owned bounded sanitization example may be allowed.");
        foreach (JsonElement item in cases)
        {
            string value = RequiredString(item, "value");
            RequiredString(item, "reason");
            bool allowed = item.GetProperty("allowed").GetBoolean();
            Assert(allowed == (value == "The request could not be completed."), $"Sanitization classification changed for '{value}'.");
            if (allowed)
            {
                Assert(value.EnumerateRunes().Count() <= 256 && value.All(character => !char.IsControl(character)), "Allowed sanitized text must be bounded and free of control characters.");
            }
        }
    }

    private static void ValidateCultureAndIdentities(string file)
    {
        using JsonDocument document = LoadDocument(file);
        JsonElement root = document.RootElement;
        Assert(root.GetProperty("expectedIdenticalAcrossCultures").GetBoolean(), "Identity outcomes must be identical across cultures.");
        Assert(!root.GetProperty("normalizationAllowed").GetBoolean() && !root.GetProperty("caseFoldingAllowed").GetBoolean(), "Protocol identities must not be normalized or case-folded.");

        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (JsonElement cultureElement in root.GetProperty("cultures").EnumerateArray())
            {
                CultureInfo culture = CultureInfo.GetCultureInfo(cultureElement.GetString()!);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                foreach (JsonElement comparison in root.GetProperty("contractComparisons").EnumerateArray())
                {
                    bool actual = string.Equals(RequiredString(comparison, "left"), RequiredString(comparison, "right"), StringComparison.Ordinal);
                    Assert(actual == comparison.GetProperty("equal").GetBoolean(), $"Ordinal contract identity result changed under culture '{culture.Name}'.");
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }

        JsonElement uuid = root.GetProperty("uuidText");
        Assert(!RequiredString(uuid, "accepted").Any(char.IsAsciiLetterUpper), "Accepted UUID text must already be lowercase.");
        Assert(RequiredString(uuid, "rejected").Any(char.IsAsciiLetterUpper), "Rejected UUID text must demonstrate uppercase rejection.");
    }

    private static string ResolveCorpusFile(string corpusRoot, string relativePath)
    {
        Assert(!Path.IsPathRooted(relativePath), $"Corpus path '{relativePath}' must be relative.");
        string fullRoot = Path.GetFullPath(corpusRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(relativePath, corpusRoot);
        StringComparison pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Assert(fullPath.StartsWith(fullRoot, pathComparison), $"Corpus path '{relativePath}' escapes the v1 corpus root.");
        Assert(File.Exists(fullPath), $"Manifest file does not exist: {relativePath}");
        string rootWithoutSeparator = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (FileSystemInfo? item = new FileInfo(fullPath); item is not null && !string.Equals(item.FullName, rootWithoutSeparator, pathComparison); item = item switch
        {
            FileInfo file => file.Directory,
            DirectoryInfo directory => directory.Parent,
            _ => null,
        })
        {
            Assert(!item.Attributes.HasFlag(FileAttributes.ReparsePoint), $"Corpus path '{relativePath}' traverses a symbolic link or reparse point.");
        }

        return fullPath;
    }

    private static bool ContainsProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName) || ContainsProperty(property.Value, propertyName))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(item => ContainsProperty(item, propertyName));
        }

        return false;
    }

    private static JsonDocument LoadDocument(string path)
    {
        return JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 128,
        });
    }

    private static string RequiredString(JsonElement element, string name)
    {
        string? value = element.GetProperty(name).GetString();
        Assert(!string.IsNullOrWhiteSpace(value), $"Property '{name}' must be a non-empty string.");
        return value!;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
