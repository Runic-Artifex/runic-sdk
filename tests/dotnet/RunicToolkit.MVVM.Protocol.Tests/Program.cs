using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RunicToolkit.MVVM;

namespace RunicToolkit.MVVM.Protocol.Tests;

internal static class Program
{
    private static int assertionCount;
    private static int testCount;
    private const string ProtocolIdentity = "runic.toolkit.mvvm/1";
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
        "observability-security",
        "projection-invariants",
        "reconnect-ack-backpressure",
        "reconnect-snapshot",
        "sanitization",
        "stale-rejection",
        "strict-codec",
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
        KeyValuePair.Create("maxDistinctAdmittedRequestsPerSessionLifetime", 65536),
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

            RunTest("contract metadata", () => RunContractMetadataTests(schemaRoot, corpusRoot));
            RunTest("schema corpus", () => RunCorpusTests(schemaRoot, corpusRoot));
            RunTest("semantic corpus", () => RunSemanticTests(corpusRoot));
            RunTest("schema adversarial matrix", () => RunSchemaAdversarialTests(schemaRoot));
            RunTest("runtime corpus conformance", () => RunRuntimeCorpusTests(corpusRoot));
            RunTest("runtime framing security", RunRuntimeFramingTests);
            RunTest("runtime validation boundaries", RunRuntimeBoundaryTests);
            RunTest("runtime semantic cross-validation", RunRuntimeSemanticValidationTests);

            Console.WriteLine($"PASS: {testCount} protocol test groups with {assertionCount} assertions.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception.Message);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void RunTest(string name, Action test)
    {
        test();
        testCount++;
        Console.WriteLine($"PASS: {name}");
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
        Assert(root.GetProperty("protocolIdentity").GetString() == ProtocolIdentity, "Manifest protocol identity must be runic.toolkit.mvvm/1.");
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

    private static void RunSchemaAdversarialTests(string schemaRoot)
    {
        using JsonSchemaEvaluator evaluator = new(schemaRoot);
        const string request = "11111111-1111-4111-8111-111111111111";
        const string session = "22222222-2222-4222-8222-222222222222";
        const string view = "33333333-3333-4333-8333-333333333333";
        const string capability = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        (string Name, string Schema, string Json, bool Valid)[] cases =
        [
            ("minimal handshake", "client-message.schema.json", $$$"""{"v":1,"kind":"handshake","request":"{{{request}}}","payload":{"supportedVersions":[1],"capabilities":[]}}""", true),
            ("unknown top-level member", "client-message.schema.json", $$$"""{"v":1,"kind":"handshake","request":"{{{request}}}","payload":{"supportedVersions":[1],"capabilities":[]},"extra":true}""", false),
            ("unknown payload member", "client-message.schema.json", $$$"""{"v":1,"kind":"handshake","request":"{{{request}}}","payload":{"supportedVersions":[1],"capabilities":[],"extra":true}}""", false),
            ("kind casing", "client-message.schema.json", $$$"""{"v":1,"kind":"Handshake","request":"{{{request}}}","payload":{"supportedVersions":[1],"capabilities":[]}}""", false),
            ("member-name casing", "client-message.schema.json", $$$"""{"v":1,"Kind":"handshake","request":"{{{request}}}","payload":{"supportedVersions":[1],"capabilities":[]}}""", false),
            ("version as decimal", "client-message.schema.json", $$$"""{"v":1.5,"kind":"handshake","request":"{{{request}}}","payload":{"supportedVersions":[1],"capabilities":[]}}""", false),
            ("version as string", "client-message.schema.json", $$$"""{"v":"1","kind":"handshake","request":"{{{request}}}","payload":{"supportedVersions":[1],"capabilities":[]}}""", false),
            ("duplicate capabilities", "client-message.schema.json", $$$"""{"v":1,"kind":"handshake","request":"{{{request}}}","payload":{"supportedVersions":[1],"capabilities":["patches","patches"]}}""", false),
            ("unknown capability casing", "client-message.schema.json", $$$"""{"v":1,"kind":"handshake","request":"{{{request}}}","payload":{"supportedVersions":[1],"capabilities":["Patches"]}}""", false),
            ("too many offered versions", "client-message.schema.json", $$$"""{"v":1,"kind":"handshake","request":"{{{request}}}","payload":{"supportedVersions":[1,1],"capabilities":[]}}""", false),
            ("nil request uuid", "client-message.schema.json", """{"v":1,"kind":"handshake","request":"00000000-0000-0000-0000-000000000000","payload":{"supportedVersions":[1],"capabilities":[]}}""", false),
            ("uppercase request uuid", "client-message.schema.json", """{"v":1,"kind":"handshake","request":"AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA","payload":{"supportedVersions":[1],"capabilities":[]}}""", false),
            ("empty contract", "client-message.schema.json", $$$"""{"v":1,"kind":"open","contract":"","view":"{{{view}}}","request":"{{{request}}}","payload":{}}""", false),
            ("control in contract", "client-message.schema.json", $$$"""{"v":1,"kind":"open","contract":"bad\ncontract","view":"{{{view}}}","request":"{{{request}}}","payload":{}}""", false),
            ("member lower bound", "client-message.schema.json", $$$"""{"v":1,"kind":"setProperty","session":"{{{session}}}","view":"{{{view}}}","request":"{{{request}}}","baseRevision":0,"capability":"{{{capability}}}","payload":{"member":1,"value":null}}""", true),
            ("zero member", "client-message.schema.json", $$$"""{"v":1,"kind":"setProperty","session":"{{{session}}}","view":"{{{view}}}","request":"{{{request}}}","baseRevision":0,"capability":"{{{capability}}}","payload":{"member":0,"value":null}}""", false),
            ("fractional member", "client-message.schema.json", $$$"""{"v":1,"kind":"setProperty","session":"{{{session}}}","view":"{{{view}}}","request":"{{{request}}}","baseRevision":0,"capability":"{{{capability}}}","payload":{"member":1.25,"value":null}}""", false),
            ("member overflow", "client-message.schema.json", $$$"""{"v":1,"kind":"setProperty","session":"{{{session}}}","view":"{{{view}}}","request":"{{{request}}}","baseRevision":0,"capability":"{{{capability}}}","payload":{"member":2147483648,"value":null}}""", false),
            ("revision upper bound", "client-message.schema.json", $$$"""{"v":1,"kind":"setProperty","session":"{{{session}}}","view":"{{{view}}}","request":"{{{request}}}","baseRevision":9223372036854775807,"capability":"{{{capability}}}","payload":{"member":1,"value":null}}""", true),
            ("revision overflow", "client-message.schema.json", $$$"""{"v":1,"kind":"setProperty","session":"{{{session}}}","view":"{{{view}}}","request":"{{{request}}}","baseRevision":9223372036854775808,"capability":"{{{capability}}}","payload":{"member":1,"value":null}}""", false),
            ("negative revision", "client-message.schema.json", $$$"""{"v":1,"kind":"ack","session":"{{{session}}}","view":"{{{view}}}","request":"{{{request}}}","capability":"{{{capability}}}","payload":{"revision":-1}}""", false),
            ("stale fault recovery fields", "host-message.schema.json", $$$"""{"v":1,"kind":"fault","session":"{{{session}}}","view":"{{{view}}}","request":"{{{request}}}","payload":{"code":"revision.stale","message":"A snapshot is required.","retryable":true,"currentRevision":7,"snapshotRequired":true}}""", true),
            ("stale fault missing revision", "host-message.schema.json", $$$"""{"v":1,"kind":"fault","session":"{{{session}}}","view":"{{{view}}}","request":"{{{request}}}","payload":{"code":"revision.stale","message":"A snapshot is required.","retryable":true,"snapshotRequired":true}}""", false),
            ("stale fault wrong retryable", "host-message.schema.json", $$$"""{"v":1,"kind":"fault","session":"{{{session}}}","view":"{{{view}}}","request":"{{{request}}}","payload":{"code":"revision.stale","message":"A snapshot is required.","retryable":false,"currentRevision":7,"snapshotRequired":true}}""", false),
            ("unknown fault casing", "host-message.schema.json", $$$"""{"v":1,"kind":"fault","request":"{{{request}}}","payload":{"code":"Request.Invalid","message":"Invalid request.","retryable":false}}""", false),
            ("empty fault message", "host-message.schema.json", $$$"""{"v":1,"kind":"fault","request":"{{{request}}}","payload":{"code":"request.invalid","message":"","retryable":false}}""", false),
        ];

        foreach ((string name, string schema, string json, bool valid) in cases)
        {
            using JsonDocument document = ParseJson(json);
            ValidationResult result = evaluator.Validate(document.RootElement, schema);
            Assert(result.IsValid == valid, $"Adversarial schema case '{name}' expected valid={valid} but got valid={result.IsValid}: {string.Join("; ", result.Errors)}");
        }
    }

    private static void RunRuntimeCorpusTests(string corpusRoot)
    {
        using JsonDocument manifest = LoadDocument(Path.Combine(corpusRoot, "manifest.json"));
        HashSet<string> clientKinds = new(StringComparer.Ordinal);
        HashSet<string> hostKinds = new(StringComparer.Ordinal);
        int validDocuments = 0;
        int invalidDocuments = 0;

        foreach (JsonElement entry in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            string id = RequiredString(entry, "id");
            string schema = RequiredString(entry, "schema");
            bool valid = entry.GetProperty("valid").GetBoolean();
            string file = ResolveCorpusFile(corpusRoot, RequiredString(entry, "file"));
            using JsonDocument fixture = LoadDocument(file);
            JsonElement[] documents = RequiredString(entry, "documentMode") switch
            {
                "single" => [fixture.RootElement.Clone()],
                "eachItem" => fixture.RootElement.EnumerateArray().Select(static item => item.Clone()).ToArray(),
                string mode => throw new InvalidDataException($"Unknown corpus document mode '{mode}'."),
            };

            foreach (JsonElement document in documents)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(document.GetRawText());
                if (!valid)
                {
                    AssertProtocolFailure(() => Decode(schema, bytes), $"Invalid corpus case '{id}' was accepted by the runtime codec.");
                    invalidDocuments++;
                    continue;
                }

                MvvmWireMessage message = Decode(schema, bytes);
                string expectedKind = RequiredString(document, "kind");
                MvvmMessageDirection expectedDirection = schema == "client"
                    ? MvvmMessageDirection.ClientToHost
                    : MvvmMessageDirection.HostToClient;
                Assert(message.Version == 1, $"Corpus case '{id}' decoded with the wrong version.");
                Assert(message.Direction == expectedDirection, $"Corpus case '{id}' decoded with the wrong direction.");
                Assert(message.Kind == expectedKind, $"Corpus case '{id}' decoded with the wrong kind.");
                Assert(JsonElement.DeepEquals(message.Payload, document.GetProperty("payload")), $"Corpus case '{id}' changed its payload during decode.");
                Assert(JsonElement.DeepEquals(message.Document, document), $"Corpus case '{id}' changed its document during decode.");

                byte[] firstEncoding = MvvmMessageCodec.Encode(message);
                byte[] secondEncoding = MvvmMessageCodec.Encode(message);
                Assert(firstEncoding.AsSpan().SequenceEqual(secondEncoding), $"Corpus case '{id}' did not encode deterministically.");
                Assert(firstEncoding.Length > 0 && firstEncoding[0] == (byte)'{' && firstEncoding[^1] == (byte)'}', $"Corpus case '{id}' encoded with a BOM, whitespace, or trailing newline.");
                Assert(!firstEncoding.Contains((byte)'\r') && !firstEncoding.Contains((byte)'\n'), $"Corpus case '{id}' encoded insignificant whitespace.");

                MvvmWireMessage roundTrip;
                try
                {
                    roundTrip = Decode(schema, firstEncoding);
                }
                catch (MvvmProtocolException exception)
                {
                    throw new InvalidDataException($"Corpus case '{id}' produced an encoding rejected by its own direction: {Encoding.UTF8.GetString(firstEncoding)}", exception);
                }
                Assert(roundTrip.Kind == message.Kind && JsonElement.DeepEquals(roundTrip.Document, message.Document), $"Corpus case '{id}' failed encode/decode round-trip.");
                AssertProtocolFailure(
                    () => _ = schema == "client" ? MvvmMessageCodec.DecodeHost(bytes) : MvvmMessageCodec.DecodeClient(bytes),
                    $"Corpus case '{id}' was accepted in the wrong direction.");

                (schema == "client" ? clientKinds : hostKinds).Add(expectedKind);
                validDocuments++;
            }
        }

        Assert(clientKinds.SetEquals(["handshake", "open", "setProperty", "execute", "cancel", "ack", "requestSnapshot", "close"]), "Runtime corpus must cover all eight client message kinds.");
        Assert(hostKinds.SetEquals(["handshakeResult", "opened", "result", "snapshot", "patch", "fault", "closed"]), "Runtime corpus must cover all seven host message kinds.");
        Assert(validDocuments == 20, $"Expected 20 valid runtime corpus documents, found {validDocuments}.");
        Assert(invalidDocuments == 17, $"Expected 17 invalid runtime corpus documents, found {invalidDocuments}.");
    }

    private static void RunRuntimeFramingTests()
    {
        byte[] valid = ClientHandshakeBytes();
        byte[] bom = [0xef, 0xbb, 0xbf, .. valid];
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(bom), "A UTF-8 BOM must be rejected.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient([0xc3, 0x28]), "Malformed UTF-8 must be rejected.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient([0xed, 0xa0, 0x80]), "UTF-8 encoded surrogate code points must be rejected.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(Encoding.UTF8.GetBytes("{}{}")), "Trailing JSON data must be rejected.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(Encoding.UTF8.GetBytes("//comment\n{}")), "JSON comments must be rejected.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(Encoding.UTF8.GetBytes("{\"v\":1,}")), "Trailing commas must be rejected.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient([]), "An empty frame must be rejected.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(Encoding.UTF8.GetBytes("   \t\r\n")), "A whitespace-only frame must be rejected.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(Encoding.UTF8.GetBytes("{\"v\":1,\"v\":1,\"kind\":\"handshake\",\"request\":\"11111111-1111-4111-8111-111111111111\",\"payload\":{\"supportedVersions\":[1],\"capabilities\":[]}}")), "Duplicate envelope properties must be rejected.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(Encoding.UTF8.GetBytes("{\"v\":1,\"kind\":\"handshake\",\"request\":\"11111111-1111-4111-8111-111111111111\",\"payload\":{\"supportedVersions\":[1],\"capabilities\":[],\"capabilities\":[]}}")), "Duplicate payload properties must be rejected.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(Encoding.UTF8.GetBytes("{\"v\":1,\"kind\":\"setProperty\",\"session\":\"22222222-2222-4222-8222-222222222222\",\"view\":\"33333333-3333-4333-8333-333333333333\",\"request\":\"11111111-1111-4111-8111-111111111111\",\"baseRevision\":0,\"capability\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"payload\":{\"member\":1,\"value\":{\"x\":1,\"x\":2}}}")), "Duplicate members nested in arbitrary JSON values must be rejected.");

        byte[] atLimit = new byte[MvvmLimits.MaximumPayloadBytes];
        valid.CopyTo(atLimit, 0);
        Array.Fill(atLimit, (byte)' ', valid.Length, atLimit.Length - valid.Length);
        MvvmWireMessage decoded = MvvmMessageCodec.DecodeClient(atLimit);
        Assert(decoded.Kind == "handshake", "A frame exactly at the hard byte ceiling must be accepted.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient([.. atLimit, (byte)' ']), "A frame above the hard byte ceiling must be rejected.");

        Assert(MvvmMessageCodec.TryDecodeClient(valid, out MvvmWireMessage? successful, out MvvmProtocolException? noError), "TryDecodeClient must return true for a valid frame.");
        Assert(successful?.Kind == "handshake" && noError is null, "Successful TryDecodeClient outputs are inconsistent.");
        Assert(!MvvmMessageCodec.TryDecodeHost(valid, out MvvmWireMessage? failed, out MvvmProtocolException? error), "TryDecodeHost must return false for a client frame.");
        Assert(failed is null && error is not null && !string.IsNullOrWhiteSpace(error.Code) && error.Path.StartsWith('$'), "Failed TryDecodeHost must return a stable error and no message.");
    }

    private static void RunRuntimeBoundaryTests()
    {
        const string request = "11111111-1111-4111-8111-111111111111";
        const string view = "33333333-3333-4333-8333-333333333333";
        string contractAtLimit = new('é', 64);
        string contractOverLimit = new('é', 65);
        string c1Contract = "bad\u0085contract";
        MvvmWireMessage contractMessage = MvvmMessageCodec.DecodeClient(Utf8($$$"""{"v":1,"kind":"open","contract":"{{{contractAtLimit}}}","view":"{{{view}}}","request":"{{{request}}}","payload":{}}"""));
        Assert(contractMessage.Kind == "open", "A contract exactly at 128 UTF-8 bytes must be accepted.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(Utf8($$$"""{"v":1,"kind":"open","contract":"{{{contractOverLimit}}}","view":"{{{view}}}","request":"{{{request}}}","payload":{}}""")), "A contract above 128 UTF-8 bytes must be rejected.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(Utf8($$$"""{"v":1,"kind":"open","contract":"{{{c1Contract}}}","view":"{{{view}}}","request":"{{{request}}}","payload":{}}""")), "C1 control characters must be rejected in contract identifiers.");

        string stringAtLimit = new('é', MvvmLimits.MaximumStringBytes / 2);
        string stringOverLimit = stringAtLimit + 'é';
        Assert(MvvmMessageCodec.DecodeClient(SetPropertyBytes(JsonSerializer.Serialize(stringAtLimit))).Kind == "setProperty", "A general string exactly at its UTF-8 byte ceiling must be accepted.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(SetPropertyBytes(JsonSerializer.Serialize(stringOverLimit))), "A general string above its UTF-8 byte ceiling must be rejected.");

        string propertyAtLimit = new('é', MvvmLimits.MaximumPropertyNameBytes / 2);
        string propertyOverLimit = propertyAtLimit + 'é';
        Assert(MvvmMessageCodec.DecodeClient(SetPropertyBytes($$$"""{"{{{propertyAtLimit}}}":null}""")).Kind == "setProperty", "A JSON property name exactly at its UTF-8 byte ceiling must be accepted.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(SetPropertyBytes($$$"""{"{{{propertyOverLimit}}}":null}""")), "A JSON property name above its UTF-8 byte ceiling must be rejected.");
        MvvmLimits loweredPropertyLimit = MvvmLimits.Default with { MaxPropertyNameBytes = 16 };
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(SetPropertyBytes("""{"abcdefghijklmnopq":null}"""), loweredPropertyLimit), "Configured lower property-name byte limits must be enforced.");

        const int maximumSanitizedMessageBytes = 256;
        string faultAtLimit = new('é', maximumSanitizedMessageBytes / 2);
        string faultOverLimit = faultAtLimit + 'é';
        Assert(MvvmMessageCodec.DecodeHost(FaultBytes(faultAtLimit)).Kind == "fault", "A fault message exactly at its UTF-8 byte ceiling must be accepted.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeHost(FaultBytes(faultOverLimit)), "A fault message above its UTF-8 byte ceiling must be rejected.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeHost(FaultBytes("bad\u0085message")), "C1 control characters must be rejected in sanitized diagnostic messages.");

        string deepValue = "0";
        for (int index = 0; index < MvvmLimits.MaximumJsonDepth; index++)
        {
            deepValue = '[' + deepValue + ']';
        }

        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(SetPropertyBytes(deepValue)), "JSON exceeding the maximum nesting depth must be rejected.");

        string objectAtLimit = "{" + string.Join(',', Enumerable.Range(0, MvvmLimits.MaximumObjectProperties).Select(static index => $"\"p{index}\":null")) + "}";
        string objectOverLimit = objectAtLimit[..^1] + ",\"overflow\":null}";
        Assert(MvvmMessageCodec.DecodeClient(SetPropertyBytes(objectAtLimit)).Kind == "setProperty", "An object exactly at its property-count ceiling must be accepted.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(SetPropertyBytes(objectOverLimit)), "An object above its property-count ceiling must be rejected.");

        string arrayAtLimit = "[" + string.Join(',', Enumerable.Repeat("null", MvvmLimits.MaximumArrayItems)) + "]";
        string arrayOverLimit = arrayAtLimit[..^1] + ",null]";
        Assert(MvvmMessageCodec.DecodeClient(SetPropertyBytes(arrayAtLimit)).Kind == "setProperty", "An array exactly at its item-count ceiling must be accepted.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(SetPropertyBytes(arrayOverLimit)), "An array above its item-count ceiling must be rejected.");
    }

    private static void RunRuntimeSemanticValidationTests()
    {
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(Utf8("""{"v":1.5,"kind":"handshake","request":"11111111-1111-4111-8111-111111111111","payload":{"supportedVersions":[1],"capabilities":[]}}""")), "The protocol version must be an integer token.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(Utf8("""{"v":1,"kind":"Handshake","request":"11111111-1111-4111-8111-111111111111","payload":{"supportedVersions":[1],"capabilities":[]}}""")), "Message kind comparison must be case-sensitive.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(Utf8("""{"v":1,"kind":"setProperty","session":"22222222-2222-4222-8222-222222222222","view":"33333333-3333-4333-8333-333333333333","request":"11111111-1111-4111-8111-111111111111","baseRevision":0,"capability":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","payload":{"member":1.5,"value":null}}""")), "Member identifiers must be integer tokens.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeHost(Utf8("""{"v":1,"kind":"patch","session":"22222222-2222-4222-8222-222222222222","view":"33333333-3333-4333-8333-333333333333","payload":{"fromRevision":5,"toRevision":7,"changes":[{"type":"property","member":1,"value":true}]}}""")), "A patch must advance exactly one revision.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeHost(Utf8("""{"v":1,"kind":"snapshot","session":"22222222-2222-4222-8222-222222222222","view":"33333333-3333-4333-8333-333333333333","request":"11111111-1111-4111-8111-111111111111","payload":{"revision":0,"members":[{"type":"property","member":2,"value":true},{"type":"property","member":1,"value":false}]}}""")), "Snapshot members must be sorted by ascending member identifier.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeHost(Utf8("""{"v":1,"kind":"snapshot","session":"22222222-2222-4222-8222-222222222222","view":"33333333-3333-4333-8333-333333333333","request":"11111111-1111-4111-8111-111111111111","payload":{"revision":0,"members":[{"type":"property","member":1,"value":true},{"type":"property","member":1,"value":false}]}}""")), "Snapshot members must not duplicate a type/member pair.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeHost(SnapshotBytes("""{"type":"validation","member":1,"errors":[]},{"type":"property","member":1,"value":true}""")), "Equal-ID snapshot members must use the binding type order.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeHost(SnapshotBytes("""{"type":"property","member":1,"value":true},{"type":"collection","member":1,"items":[]}""")), "A snapshot member identifier must have only one principal binding kind.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeHost(SnapshotBytes("""{"type":"validation","member":1,"errors":[]}""")), "Validation state must not be orphaned from a principal binding member.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeHost(SnapshotBytes("""{"type":"command","member":1,"canExecute":true,"isExecuting":false},{"type":"validation","member":1,"errors":[]}""")), "Validation state must not attach to a command member.");
        Assert(MvvmMessageCodec.DecodeHost(SnapshotBytes("""{"type":"property","member":1,"value":true},{"type":"validation","member":1,"errors":[]}""")).Kind == "snapshot", "Validation may follow its principal binding at the same member identifier.");
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeHost(Utf8("""{"v":1,"kind":"patch","session":"22222222-2222-4222-8222-222222222222","view":"33333333-3333-4333-8333-333333333333","payload":{"fromRevision":9223372036854775807,"toRevision":9223372036854775807,"changes":[{"type":"property","member":1,"value":true}]}}""")), "Patch revision validation must not overflow Int64.");
        foreach (string operation in new[] { "insert", "remove", "replace" })
        {
            AssertProtocolFailure(() => MvvmMessageCodec.DecodeHost(CollectionPatchBytes(operation, "[]")), $"Collection {operation} must contain at least one item.");
        }

        Assert(MvvmMessageCodec.DecodeHost(CollectionPatchBytes("reset", "[]")).Kind == "patch", "Collection reset may authoritatively replace a collection with an empty state.");

        byte[] reordered = Utf8("""{"payload":{"capabilities":["cancellation","patches"],"supportedVersions":[1]},"request":"11111111-1111-4111-8111-111111111111","kind":"handshake","v":1}""");
        MvvmWireMessage message = MvvmMessageCodec.DecodeClient(reordered);
        string encoded = Encoding.UTF8.GetString(MvvmMessageCodec.Encode(message));
        Assert(encoded == """{"v":1,"kind":"handshake","request":"11111111-1111-4111-8111-111111111111","payload":{"supportedVersions":[1],"capabilities":["cancellation","patches"]}}""", "Encoding must use schema property order and ordinal capability order.");
        AssertProtocolFailure(
            () => MvvmMessageCodec.DecodeClient(Utf8("""{"v":1,"kind":"handshake","request":"11111111-1111-4111-8111-111111111111","payload":{"supportedVersions":[1],"capabilities":["patches","cancellation"]}}""")),
            "Client capability arrays must already use ordinal order.");
        AssertProtocolFailure(
            () => MvvmMessageCodec.DecodeHost(Utf8("""{"v":1,"kind":"handshakeResult","request":"11111111-1111-4111-8111-111111111111","payload":{"selectedVersion":1,"capabilities":["patches","cancellation"],"limits":{"maxFrameBytes":1048576,"maxJsonDepth":32,"maxSessions":16,"maxPendingRequests":64,"maxSnapshotMembers":4096,"maxPatchChanges":1024,"maxCollectionItems":10000,"commandTimeoutMilliseconds":30000}}}""")),
            "Host capability arrays must already use ordinal order.");

        MvvmWireMessage applicationObject = MvvmMessageCodec.DecodeClient(SetPropertyBytes("""{"z":1.0,"v":2,"a":{"z":2,"v":3,"a":1e2}}"""));
        string applicationEncoding = Encoding.UTF8.GetString(MvvmMessageCodec.Encode(applicationObject));
        Assert(applicationEncoding.EndsWith("\"payload\":{\"member\":1,\"value\":{\"a\":{\"a\":100,\"v\":3,\"z\":2},\"v\":2,\"z\":1}}}", StringComparison.Ordinal), "Encoding must recursively sort application object names ordinally even when they collide with schema names, and use shortest base-10 numbers.");

        MvvmLimits lowerLimit = MvvmLimits.Default with { MaxPayloadBytes = 64 };
        AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(ClientHandshakeBytes(), lowerLimit), "Configured lower frame limits must be enforced on decode.");
        AssertProtocolFailure(() => MvvmMessageCodec.Encode(message, lowerLimit), "Configured lower frame limits must be enforced on encode.");

        MvvmWireMessage longString = MvvmMessageCodec.DecodeClient(SetPropertyBytes(JsonSerializer.Serialize(new string('x', 45))));
        AssertProtocolFailure(() => MvvmMessageCodec.Encode(longString, MvvmLimits.Default with { MaxStringBytes = 44 }), "Encoding must recheck configured lower string byte limits.");
        MvvmWireMessage longProperty = MvvmMessageCodec.DecodeClient(SetPropertyBytes("""{"abcdefghijklmnopq":null}"""));
        AssertProtocolFailure(() => MvvmMessageCodec.Encode(longProperty, MvvmLimits.Default with { MaxPropertyNameBytes = 16 }), "Encoding must recheck configured lower property-name byte limits.");
        string elevenProperties = "{" + string.Join(',', Enumerable.Range(0, 11).Select(static index => $"\"p{index}\":null")) + "}";
        MvvmWireMessage largeObject = MvvmMessageCodec.DecodeClient(SetPropertyBytes(elevenProperties));
        AssertProtocolFailure(() => MvvmMessageCodec.Encode(largeObject, MvvmLimits.Default with { MaxObjectProperties = 10 }), "Encoding must recheck configured lower object-property limits.");
        MvvmWireMessage largeArray = MvvmMessageCodec.DecodeClient(SetPropertyBytes("[null,null,null,null]"));
        AssertProtocolFailure(() => MvvmMessageCodec.Encode(largeArray, MvvmLimits.Default with { MaxArrayItems = 3 }), "Encoding must recheck configured lower array-item limits.");
        MvvmWireMessage deepMessage = MvvmMessageCodec.DecodeClient(SetPropertyBytes("[[0]]"));
        AssertProtocolFailure(() => MvvmMessageCodec.Encode(deepMessage, MvvmLimits.Default with { MaxJsonDepth = 3 }), "Encoding must recheck configured lower JSON depth limits with parser-consistent root semantics.");

        MvvmWireMessage exponentBomb = MvvmMessageCodec.DecodeClient(SetPropertyBytes("1e999999999999999999999999999999999999"));
        AssertProtocolFailure(() => MvvmMessageCodec.Encode(exponentBomb), "Canonical number encoding must reject a significant exponent too large to bound safely.");
        MvvmWireMessage leadingZeroExponent = MvvmMessageCodec.DecodeClient(SetPropertyBytes("1e0000000000000000000000000000000000000002"));
        string normalizedExponent = Encoding.UTF8.GetString(MvvmMessageCodec.Encode(leadingZeroExponent));
        Assert(normalizedExponent.EndsWith("\"value\":100}}", StringComparison.Ordinal), "Canonical number encoding must ignore arbitrarily many leading exponent zeros and emit the shortest base-10 value.");
    }

    private static MvvmWireMessage Decode(string schema, byte[] bytes) => schema switch
    {
        "client" => MvvmMessageCodec.DecodeClient(bytes),
        "host" => MvvmMessageCodec.DecodeHost(bytes),
        _ => throw new InvalidDataException($"Unknown runtime corpus schema '{schema}'."),
    };

    private static byte[] ClientHandshakeBytes() => Utf8("""{"v":1,"kind":"handshake","request":"11111111-1111-4111-8111-111111111111","payload":{"supportedVersions":[1],"capabilities":[]}}""");

    private static byte[] SetPropertyBytes(string value) => Utf8($$$"""{"v":1,"kind":"setProperty","session":"22222222-2222-4222-8222-222222222222","view":"33333333-3333-4333-8333-333333333333","request":"11111111-1111-4111-8111-111111111111","baseRevision":0,"capability":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","payload":{"member":1,"value":{{{value}}}}}""");

    private static byte[] FaultBytes(string message) => Utf8($$$"""{"v":1,"kind":"fault","request":"11111111-1111-4111-8111-111111111111","payload":{"code":"request.invalid","message":"{{{message}}}","retryable":false}}""");

    private static byte[] SnapshotBytes(string members) => Utf8($$$"""{"v":1,"kind":"snapshot","session":"22222222-2222-4222-8222-222222222222","view":"33333333-3333-4333-8333-333333333333","request":"11111111-1111-4111-8111-111111111111","payload":{"revision":0,"members":[{{{members}}}]}}""");

    private static byte[] CollectionPatchBytes(string operation, string items) => Utf8($$$"""{"v":1,"kind":"patch","session":"22222222-2222-4222-8222-222222222222","view":"33333333-3333-4333-8333-333333333333","payload":{"fromRevision":0,"toRevision":1,"changes":[{"type":"collection","member":1,"operation":"{{{operation}}}","index":0,"items":{{{items}}}}]}}""");

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static void AssertProtocolFailure(Action action, string message)
    {
        try
        {
            action();
        }
        catch (MvvmProtocolException exception)
        {
            Assert(!string.IsNullOrWhiteSpace(exception.Code), "Protocol failures must expose a stable error code.");
            Assert(!string.IsNullOrWhiteSpace(exception.Path) && exception.Path[0] == '$', "Protocol failures must expose a rooted bounded path.");
            return;
        }

        Assert(false, message);
    }

    private static void RunSemanticTests(string corpusRoot)
    {
        using JsonDocument manifest = LoadDocument(Path.Combine(corpusRoot, "manifest.json"));
        Dictionary<string, string> fixtures = manifest.RootElement.GetProperty("semanticCases").EnumerateArray()
            .ToDictionary(
                item => RequiredString(item, "id"),
                item => ResolveCorpusFile(corpusRoot, RequiredString(item, "file")),
                StringComparer.Ordinal);
        Assert(fixtures.Keys.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(ExpectedSemanticCaseIds, StringComparer.Ordinal), "Manifest must register the exact twelve v1 semantic cases.");

        ValidateSuccessfulMutation(fixtures["successful-mutation"]);
        ValidateStaleRejection(fixtures["stale-rejection"]);
        ValidateCancellationAndTimeout(fixtures["cancellation-and-timeout"]);
        ValidateReconnectSnapshot(fixtures["reconnect-snapshot"]);
        ValidateLimits(fixtures["limits"]);
        ValidateFaultCatalog(fixtures["fault-catalog"]);
        ValidateSanitization(fixtures["sanitization"]);
        ValidateCultureAndIdentities(fixtures["culture-and-identities"]);
        ValidateStrictCodec(fixtures["strict-codec"]);
        ValidateProjectionInvariants(fixtures["projection-invariants"]);
        ValidateReconnectAckBackpressure(fixtures["reconnect-ack-backpressure"]);
        ValidateObservabilitySecurity(fixtures["observability-security"]);
        Console.WriteLine("PASS: validated all 12 registered semantic fixtures, including strict framing, projection, backpressure, and security semantics.");
    }

    private static void ValidateStrictCodec(string file)
    {
        using JsonDocument document = LoadDocument(file);
        JsonElement root = document.RootElement;
        string[] expectedIds =
        [
            "invalid-utf8", "utf8-bom", "duplicate-envelope-name", "duplicate-nested-name",
            "escaped-duplicate-name", "lone-surrogate-escape", "trailing-document", "comment",
            "trailing-comma", "non-finite-number",
        ];
        JsonElement[] cases = root.GetProperty("cases").EnumerateArray().ToArray();
        Assert(cases.Select(item => RequiredString(item, "id")).SequenceEqual(expectedIds, StringComparer.Ordinal), "strict-codec must retain the complete ordered framing attack matrix.");
        foreach (JsonElement item in cases)
        {
            byte[] bytes = item.TryGetProperty("encodedHex", out JsonElement hex)
                ? Convert.FromHexString(hex.GetString()!)
                : Utf8(RequiredString(item, "utf8Text"));
            Assert(RequiredString(item, "expected") == "rejectFrame", "Strict framing attacks must reject the complete frame.");
            Assert(item.GetProperty("consumerInvocations").GetInt32() == 0, "Strict framing rejection must happen before consumer invocation.");
            AssertProtocolFailure(() => MvvmMessageCodec.DecodeClient(bytes), $"Strict framing case '{RequiredString(item, "id")}' was accepted.");
        }

        JsonElement[] byteBoundaries = root.GetProperty("utf8ByteBoundaries").EnumerateArray().ToArray();
        Assert(byteBoundaries.Length == 2, "strict-codec must contain the accepted and rejected multibyte contract boundaries.");
        foreach (JsonElement boundary in byteBoundaries)
        {
            string value = RequiredString(boundary, "value");
            int recordedBytes = boundary.GetProperty("utf8Bytes").GetInt32();
            Assert(Encoding.UTF8.GetByteCount(value) == recordedBytes, $"Strict codec boundary '{RequiredString(boundary, "id")}' records the wrong UTF-8 size.");
            Assert((recordedBytes <= 128) == (RequiredString(boundary, "expected") == "accepted"), "Strict codec contract boundary classification changed.");
        }

        JsonElement canonical = root.GetProperty("canonicalEncoding");
        Assert(RequiredString(canonical, "propertyOrder") == "schema", "Canonical envelope and binding property order must follow the schema.");
        Assert(RequiredString(canonical, "applicationObjectPropertyOrder") == "ordinalRecursive", "Canonical application objects must sort property names recursively and ordinally.");
        Assert(RequiredString(canonical, "uuidText") == "lowercaseHyphenated", "Canonical UUID text must be lowercase and hyphenated.");
        Assert(RequiredString(canonical, "numberSpelling") == "shortestValidBase10", "Canonical number spelling must be the shortest valid base-10 representation.");
        Assert(!canonical.GetProperty("insignificantWhitespace").GetBoolean(), "Canonical wire encoding must omit insignificant whitespace.");
        Assert(canonical.GetProperty("fileTrailingNewlineCount").GetInt32() == 1, "Canonical corpus files must have one trailing newline.");
    }

    private static void ValidateProjectionInvariants(string file)
    {
        using JsonDocument document = LoadDocument(file);
        JsonElement root = document.RootElement;
        JsonElement vocabulary = root.GetProperty("bindingVocabulary");
        Assert(vocabulary.GetProperty("snapshotMemberTypes").EnumerateArray().Select(static item => item.GetString()).SequenceEqual(["property", "collection", "command", "validation"]), "Snapshot vocabulary changed.");
        Assert(vocabulary.GetProperty("patchChangeTypes").EnumerateArray().Select(static item => item.GetString()).SequenceEqual(["property", "collection", "collectionMove", "command", "validation"]), "Patch vocabulary changed.");
        Assert(vocabulary.GetProperty("collectionOperations").EnumerateArray().Select(static item => item.GetString()).SequenceEqual(["insert", "remove", "replace", "reset"]), "Collection operation vocabulary changed.");

        JsonElement snapshot = root.GetProperty("snapshotRules");
        Assert(RequiredString(snapshot, "memberOrder") == "ascendingNumericMemberThenTypeOrder", "Snapshot ordering must be deterministic.");
        Assert(!snapshot.GetProperty("duplicateTypeMemberPairAllowed").GetBoolean(), "Snapshots must reject duplicate type/member pairs.");
        Assert(snapshot.GetProperty("replacesPropertiesCollectionsCommandsAndValidation").GetBoolean(), "Snapshots must replace every projected binding category.");

        Dictionary<string, string> expected = new(StringComparer.Ordinal)
        {
            ["consecutive"] = "applyAtomically",
            ["byte-equivalent-duplicate"] = "ignore",
            ["conflicting-duplicate"] = "requestSnapshot",
            ["revision-gap"] = "requestSnapshot",
            ["non-consecutive-transition"] = "requestSnapshot",
        };
        JsonElement[] patchCases = root.GetProperty("patchCases").EnumerateArray().ToArray();
        Assert(patchCases.Length == expected.Count, "Projection fixture must cover the exact patch sequencing cases.");
        foreach (JsonElement item in patchCases)
        {
            string id = RequiredString(item, "id");
            Assert(expected.Remove(id, out string? outcome) && RequiredString(item, "expected") == outcome, $"Patch case '{id}' has an unexpected outcome.");
            long from = item.GetProperty("fromRevision").GetInt64();
            long to = item.GetProperty("toRevision").GetInt64();
            Assert((to == from + 1) == (id != "non-consecutive-transition"), $"Patch case '{id}' no longer represents its named transition.");
        }

        Assert(expected.Count == 0, "Projection fixture omitted a required patch sequencing case.");
        Assert(RequiredString(root, "collectionIndexBasis") == "stateAfterEarlierChangesInSamePatch", "Collection patch indices must observe earlier changes in the transaction.");
        Assert(root.GetProperty("transactionChangeOrderPreserved").GetBoolean(), "Patch transaction change order must be preserved.");
    }

    private static void ValidateReconnectAckBackpressure(string file)
    {
        using JsonDocument document = LoadDocument(file);
        JsonElement root = document.RootElement;
        JsonElement reconnect = root.GetProperty("reconnect");
        Assert(reconnect.GetProperty("steps").EnumerateArray().Select(static item => item.GetString()).SequenceEqual(["handshake", "requestSnapshot", "replaceLocalState", "resumeMutations"]), "Reconnect steps must require snapshot replacement before mutations resume.");
        Assert(!reconnect.GetProperty("patchReplayRequired").GetBoolean() && !reconnect.GetProperty("mutationBeforeSnapshotAllowed").GetBoolean(), "Reconnect must not require replay or permit mutation before recovery.");

        foreach (JsonElement ack in root.GetProperty("ackCases").EnumerateArray())
        {
            long host = ack.GetProperty("hostRevision").GetInt64();
            long previous = ack.GetProperty("previousAcknowledgedRevision").GetInt64();
            long requested = ack.GetProperty("requestedRevision").GetInt64();
            long expected = ack.GetProperty("expectedAcknowledgedRevision").GetInt64();
            Assert(expected == (requested <= host ? Math.Max(previous, requested) : previous), $"Ack case '{RequiredString(ack, "id")}' violates monotonic bounded acknowledgement.");
            Assert(ack.GetProperty("revisionIncrements").GetInt32() == 0, "Acknowledgement must never advance authoritative revision.");
        }

        JsonElement queue = root.GetProperty("pendingQueue");
        Assert(queue.GetProperty("capacity").GetInt32() == MvvmLimits.MaximumPendingRequests, "Pending queue fixture must match the hard runtime capacity.");
        Assert(RequiredString(queue, "atCapacity") == "accepted" && RequiredString(queue, "aboveCapacity") == "limit.exceeded", "Pending admission boundary classification changed.");
        Assert(queue.GetProperty("consumerInvocationsForRejectedAdmission").GetInt32() == 0 && queue.GetProperty("revisionIncrementsForRejectedAdmission").GetInt32() == 0, "Backpressure rejection must occur before dispatch or revision advancement.");

        JsonElement writer = root.GetProperty("boundedTransportWriter");
        Assert(writer.GetProperty("finiteConfiguredBoundRequired").GetBoolean() && writer.GetProperty("terminalCapacityReservedForEveryAdmittedRequest").GetBoolean(), "Transport writer must be bounded while reserving terminal capacity.");
        Assert(writer.GetProperty("droppableOrReplaceableMessages").GetArrayLength() == 0, "Protocol messages must not be dropped or replaced under pressure.");
        Assert(RequiredString(writer, "writeTimeoutOutcome") == "closeTransport", "A bounded writer timeout must close the transport.");

        Assert(root.GetProperty("ackIsAdvisoryBackpressureOnly").GetBoolean() && root.GetProperty("ackPermitsReclamation").GetBoolean() && !root.GetProperty("ackPromisesReplay").GetBoolean(), "Ack must remain advisory and must not promise replay.");
    }

    private static void ValidateObservabilitySecurity(string file)
    {
        using JsonDocument document = LoadDocument(file);
        JsonElement root = document.RootElement;
        JsonElement observability = root.GetProperty("observability");
        Assert(observability.GetProperty("bclSurfaces").EnumerateArray().Select(static item => item.GetString()).SequenceEqual(["ActivitySource", "Meter"]), "Observability must remain BCL-friendly.");
        Assert(!observability.GetProperty("eventSourceRequired").GetBoolean(), "EventSource must not be required when ActivitySource and Meter provide the v1 BCL surface.");
        Assert(observability.GetProperty("allowedDimensions").GetArrayLength() == 4 && observability.GetProperty("forbiddenValues").GetArrayLength() == 9, "Telemetry cardinality and security vocabulary changed.");
        Assert(observability.GetProperty("limitDimensionValues").EnumerateArray().Select(static item => item.GetString()).SequenceEqual(["sessions", "requests", "cancellation-control", "request-ledger"]), "Limit telemetry values must remain bounded.");
        Assert(observability.GetProperty("boundedCardinalityRequired").GetBoolean() && observability.GetProperty("sanitizeBeforeLogging").GetBoolean(), "Telemetry must be bounded and sanitized before publication.");

        JsonElement capability = root.GetProperty("capability");
        Assert(capability.GetProperty("decodedBytes").GetInt32() == 32 && capability.GetProperty("wireCharacters").GetInt32() == 43, "Capability token representation changed.");
        Assert(RequiredString(capability, "comparison") == "constantTime", "Capability comparison must be constant-time.");
        Assert(capability.GetProperty("exposedByHostKinds").EnumerateArray().Single().GetString() == "opened" && !capability.GetProperty("echoedByFaultsLogsMetricsOrTraces").GetBoolean(), "Only opened may expose a capability token.");

        JsonElement replay = root.GetProperty("requestReplay");
        Assert(replay.GetProperty("requestIdUniqueForSessionLifetime").GetBoolean() && replay.GetProperty("completedIdsRetainedUntilTombstoneRelease").GetBoolean(), "Request replay protection must span the session tombstone lifetime.");
        Assert(RequiredString(replay, "replayFault") == "request.invalid" && replay.GetProperty("consumerInvocations").GetInt32() == 0, "Request replay must fault before consumer invocation.");
        Assert(replay.GetProperty("maxDistinctAdmittedRequestsPerSessionLifetime").GetInt32() == 65_536 && !replay.GetProperty("perEntryEvictionAllowed").GetBoolean(), "Replay retention must have a fixed non-evicting session-lifetime bound.");
        Assert(RequiredString(replay, "afterRequest65536Completes") == "closeSession" && RequiredString(replay, "laterRequestFault") == "session.closed" && replay.GetProperty("closedSessionRuleTakesPrecedence").GetBoolean(), "Replay-budget exhaustion must transition to the closed-session rule.");

        JsonElement mismatch = root.GetProperty("identityMismatch");
        Assert(!mismatch.GetProperty("distinguishSessionViewOrCapabilityFailure").GetBoolean() && !mismatch.GetProperty("echoAttackerControlledContent").GetBoolean(), "Identity mismatch failures must be indistinguishable and sanitized.");
        JsonElement tombstone = root.GetProperty("closeTombstone");
        Assert(tombstone.GetProperty("authenticatedCloseReplayReturnsSameTerminalState").GetBoolean() && RequiredString(tombstone, "otherRequestsReturn") == "session.closed" && !tombstone.GetProperty("postExpiryValidityDisclosure").GetBoolean(), "Close tombstone security semantics changed.");
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

    private static JsonDocument ParseJson(string json) => JsonDocument.Parse(json, new JsonDocumentOptions
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 128,
    });

    private static string RequiredString(JsonElement element, string name)
    {
        string? value = element.GetProperty(name).GetString();
        Assert(!string.IsNullOrWhiteSpace(value), $"Property '{name}' must be a non-empty string.");
        return value!;
    }

    private static void Assert(bool condition, string message)
    {
        assertionCount++;
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
