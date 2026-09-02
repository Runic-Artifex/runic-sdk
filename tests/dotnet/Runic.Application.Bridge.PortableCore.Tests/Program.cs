using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.PortableCore.Contract;

var snapshot = new PortableSnapshot
{
    Pair = new PortableSnapshotPair { Item1 = "count", Item2 = 2 },
    OptionalPair = new PortableSnapshotOptionalPair { Item1 = "optional" },
    ValuesByName = new Dictionary<string, long> { ["one"] = 1 },
    Choice = PortableSnapshotChoice.FromCase1(new PortableSnapshotChoice0 { Tag = "TextChoice", Value = "selected" }),
    UniqueChoices = [new PortableSnapshotUniqueChoicesItem { Tag = "TextChoice", Value = "unique" }],
    Node = new RecursiveNode { Value = "root" },
    NullableNote = null,
};
var encoded = PortableCoreBridgeContractCodec.EncodePortableSnapshot(snapshot);
if (encoded.GetProperty("pair").GetArrayLength() != 2 ||
    encoded.GetProperty("optionalPair").GetArrayLength() != 1 ||
    encoded.GetProperty("choice").GetProperty("_tag").GetString() != "TextChoice" ||
    encoded.GetProperty("nullableNote").ValueKind != System.Text.Json.JsonValueKind.Null)
{
    throw new InvalidOperationException("Portable-core encoding failed.");
}
PortableSnapshot decoded = PortableCoreBridgeContractCodec.DecodePortableSnapshot(encoded);
if (decoded.Pair.Item2 != 2 || decoded.OptionalPair.Item2.HasValue || decoded.Node.Next.HasValue)
{
    throw new InvalidOperationException("Portable-core decoding failed.");
}
try
{
    _ = PortableCoreBridgeContractCodec.EncodePortableSnapshot(snapshot with
    {
        UniqueChoices =
        [
            new PortableSnapshotUniqueChoicesItem { Tag = "TextChoice", Value = "duplicate" },
            new PortableSnapshotUniqueChoicesItem { Tag = "TextChoice", Value = "duplicate" },
        ],
    });
    throw new InvalidOperationException("Duplicate structured collection values were accepted.");
}
catch (System.Text.Json.JsonException)
{
}
var failure = PortableCoreBridgeErrors.QuotaExceeded(new QuotaExceeded { Tag = "QuotaExceeded", Limit = 2 });
var validatedFailure = new PortableCoreBridgeDispatcher(new PortableCoreHandler()).ValidateError(failure.Error);
if (validatedFailure.GetProperty("_tag").GetString() != "QuotaExceeded" ||
    validatedFailure.GetProperty("limit").GetInt64() != 2)
{
    throw new InvalidOperationException("Declared application-error encoding failed.");
}
Console.WriteLine("PASS: generated tuple, record, union, nullable, optional, and recursive codecs agree.");

file sealed class PortableCoreHandler : IPortableCoreBridgeHandler
{
    public ValueTask<ApplicationInitialized> InitializeApplicationAsync(
        InitializeApplication command,
        Runic.Application.Bridge.BridgeCommandContext context,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}
