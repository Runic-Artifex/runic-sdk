using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.MVVM;
using WebUIToolkit.MVVM.CommunityToolkit;

namespace WebUIToolkit.MVVM.CommunityToolkit.Tests;

internal static partial class Program
{
    private const string SessionId = "22222222-2222-4222-8222-222222222222";
    private const string ViewId = "33333333-3333-4333-8333-333333333333";
    private const string RequestId = "11111111-1111-4111-8111-111111111111";
    private const string Capability = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static async Task RunCommunityToolkitG3EvidenceAsync()
    {
        await RunG3Async("host-binding-vocabulary", CommunityToolkitHostBindingVocabularyAsync);
        await RunG3Async("successful-mutation", CommunityToolkitSuccessfulMutationAsync);
        await RunG3Async("projection-invariants", CommunityToolkitProjectionInvariantsAsync);
        await RunG3Async("cancellation-and-timeout", CommunityToolkitCancellationAsync);
        await RunG3Async("limits", CommunityToolkitLimitsAsync);
        await RunG3Async("reconnect-snapshot", CommunityToolkitReconnectSnapshotAsync);
        await RunG3Async("reconnect-ack-backpressure", CommunityToolkitAckIndependenceAsync);
        await RunG3Async("strict-codec", CommunityToolkitStrictCodecAsync);
        await RunG3Async("observability-security", CommunityToolkitObservabilitySecurityAsync);
    }

    private static async Task RunG3Async(string corpusId, Func<Task> scenario)
    {
        await scenario().ConfigureAwait(false);
        Console.WriteLine($"G3-EVIDENCE: communitytoolkit/{corpusId}");
    }

    private static async Task CommunityToolkitHostBindingVocabularyAsync()
    {
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> adapter =
            CreateAdapter(new FixtureViewModel());
        MvvmSnapshot snapshot = await adapter.SnapshotAsync(CancellationToken.None);
        JsonElement[] members = SnapshotMembers(snapshot);

        Equal("1:Property,2:Command,3:Command,4:Property,5:Command",
            string.Join(',', adapter.Vocabulary.Members.Select(
                static member => $"{member.MemberId}:{member.Kind}")));
        Equal(
            "1:property,1:validation,2:command,3:command,4:property,5:command",
            string.Join(',', members.Select(MemberKey)));
        string wireProjection = snapshot.State.GetRawText();
        False(wireProjection.Contains(nameof(FixtureViewModel), StringComparison.Ordinal));
        False(wireProjection.Contains("CommunityToolkit", StringComparison.Ordinal));
        False(wireProjection.Contains(nameof(FixtureViewModel.Name), StringComparison.Ordinal));
        False(wireProjection.Contains(nameof(FixtureViewModel.SubmitCommand), StringComparison.Ordinal));
    }

    private static async Task CommunityToolkitSuccessfulMutationAsync()
    {
        var viewModel = new FixtureViewModel();
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> adapter =
            CreateAdapter(viewModel);
        MvvmBindingResult result = await adapter.DispatchAsync(
            PropertyMutation(1, Json("null")),
            CancellationToken.None);

        True(result.Succeeded);
        True(result.Committed);
        True(viewModel.Name is null);
        Equal(5, result.Patches.Count);
        Equal(
            "1:Property,1:Validation,2:Command,3:Command,5:Command",
            string.Join(',', result.Patches.Select(
                static patch => $"{patch.MemberId}:{patch.Kind}")));
        Equal(1, result.Patches.OfType<MvvmPropertyPatch>().Count());
        Equal(1, result.Patches.OfType<MvvmValidationPatch>().Count());
        True(result.Patches.OfType<MvvmValidationPatch>().Single().Errors.Count > 0);
        Equal(3, result.Patches.OfType<MvvmCommandPatch>().Count());
    }

    private static async Task CommunityToolkitProjectionInvariantsAsync()
    {
        var viewModel = new FixtureViewModel();
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> adapter =
            CreateAdapter(viewModel);
        MvvmSnapshot first = await adapter.SnapshotAsync(CancellationToken.None);
        MvvmSnapshot second = await adapter.SnapshotAsync(CancellationToken.None);
        Equal(first.State.GetRawText(), second.State.GetRawText());

        string[] keys = SnapshotMembers(first).Select(MemberKey).ToArray();
        Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Equal(
            "1:property,1:validation,2:command,3:command,4:property,5:command",
            string.Join(',', keys));

        MvvmBindingResult changed = await adapter.DispatchAsync(
            PropertyMutation(4, Json("true")),
            CancellationToken.None);
        True(changed.Committed);
        Equal(changed.Patches.Count, changed.Patches
            .Select(static patch => $"{patch.MemberId}:{patch.Kind}")
            .Distinct(StringComparer.Ordinal)
            .Count());
        MvvmSnapshot recovered = await adapter.SnapshotAsync(CancellationToken.None);
        JsonElement canSubmit = SnapshotMembers(recovered)
            .Single(static member =>
                member.GetProperty("member").GetInt32() == 4 &&
                member.GetProperty("type").GetString() == "property");
        True(canSubmit.GetProperty("value").GetBoolean());
    }

    private static async Task CommunityToolkitCancellationAsync()
    {
        var viewModel = new FixtureViewModel();
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> adapter =
            CreateAdapter(viewModel);
        string before = (await adapter.SnapshotAsync(CancellationToken.None)).State.GetRawText();
        using var cancellation = new CancellationTokenSource();
        Task<MvvmBindingResult> pending = adapter.DispatchAsync(
            CommandMutation(5, Json("null")),
            cancellation.Token).AsTask();
        await viewModel.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () => await pending.ConfigureAwait(false));
        True(viewModel.Cancelled);
        string after = (await adapter.SnapshotAsync(CancellationToken.None)).State.GetRawText();
        Equal(before, after);
    }

    private static async Task CommunityToolkitLimitsAsync()
    {
        byte[] admittedFrame = SetPropertyFrame("\"bounded\"");
        var limits = MvvmLimits.Default with { MaxPayloadBytes = admittedFrame.Length };
        var probe = new AdmissionProbe();
        var viewModel = new FixtureViewModel();
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> adapter =
            CreateAdapter(viewModel);

        MvvmBindingResult result = await DispatchRawAsync(admittedFrame, adapter, probe, limits);
        True(result.Committed);
        Equal(1, probe.Invocations);
        True(result.Patches.Count <= limits.MaxPatchOperations);

        byte[] oversized = [.. admittedFrame, (byte)' '];
        await ThrowsAsync<MvvmProtocolException>(
            async () => await DispatchRawAsync(oversized, adapter, probe, limits));
        Equal(1, probe.Invocations);

        MvvmSnapshot snapshot = await adapter.SnapshotAsync(CancellationToken.None);
        JsonElement[] members = SnapshotMembers(snapshot);
        True(members.Length <= MvvmLimits.Default.MaxSnapshotMembers);
        byte[] encodedSnapshot = HostSnapshotFrame(snapshot);
        _ = MvvmMessageCodec.DecodeHost(
            encodedSnapshot,
            MvvmLimits.Default with { MaxSnapshotMembers = members.Length });
        Throws<MvvmProtocolException>(() => MvvmMessageCodec.DecodeHost(
            encodedSnapshot,
            MvvmLimits.Default with { MaxSnapshotMembers = members.Length - 1 }));
    }

    private static async Task CommunityToolkitReconnectSnapshotAsync()
    {
        var viewModel = new FixtureViewModel { Name = "authoritative" };
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> adapter =
            CreateAdapter(viewModel);
        var localState = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stale:transport-only"] = "\"must disappear\"",
            ["1:property"] = "\"stale title\"",
        };

        MvvmSnapshot reconnect = await adapter.SnapshotAsync(CancellationToken.None);
        Dictionary<string, string> authoritative = SnapshotMembers(reconnect)
            .ToDictionary(MemberKey, static member => member.GetRawText(), StringComparer.Ordinal);
        localState.Clear();
        foreach ((string key, string value) in authoritative)
        {
            localState.Add(key, value);
        }

        False(localState.ContainsKey("stale:transport-only"));
        Equal(authoritative.Count, localState.Count);
        True(localState["1:property"].Contains("authoritative", StringComparison.Ordinal));
    }

    private static async Task CommunityToolkitAckIndependenceAsync()
    {
        long advisoryHighWatermark = 0;
        foreach (long revision in new long[] { 2, 8, 5 })
        {
            MvvmWireMessage ack = MvvmMessageCodec.DecodeClient(AckFrame(revision));
            advisoryHighWatermark = Math.Max(
                advisoryHighWatermark,
                ack.Document.GetProperty("payload").GetProperty("revision").GetInt64());
        }

        Equal(8L, advisoryHighWatermark);
        var acknowledgedModel = new FixtureViewModel();
        var controlModel = new FixtureViewModel();
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> acknowledged =
            CreateAdapter(acknowledgedModel);
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> control =
            CreateAdapter(controlModel);
        MvvmBindingResult afterAcks = await acknowledged.DispatchAsync(
            PropertyMutation(1, Json("\"same\"")),
            CancellationToken.None);
        MvvmBindingResult withoutAcks = await control.DispatchAsync(
            PropertyMutation(1, Json("\"same\"")),
            CancellationToken.None);

        Equal(
            string.Join(',', afterAcks.Patches.Select(static patch => $"{patch.MemberId}:{patch.Kind}")),
            string.Join(',', withoutAcks.Patches.Select(static patch => $"{patch.MemberId}:{patch.Kind}")));
        Equal(
            (await acknowledged.SnapshotAsync(CancellationToken.None)).State.GetRawText(),
            (await control.SnapshotAsync(CancellationToken.None)).State.GetRawText());
        False(typeof(CommunityToolkitMvvmBindingAdapter<>).Assembly.GetReferencedAssemblies()
            .Any(static assembly => assembly.Name is "WebUIToolkit.Hosting"));
    }

    private static async Task CommunityToolkitStrictCodecAsync()
    {
        byte[] canonicalInput = SetPropertyFrame("\"codec-first\"");
        MvvmWireMessage decoded = MvvmMessageCodec.DecodeClient(canonicalInput);
        byte[] firstEncoding = MvvmMessageCodec.Encode(decoded);
        byte[] secondEncoding = MvvmMessageCodec.Encode(decoded);
        True(firstEncoding.SequenceEqual(secondEncoding));

        var probe = new AdmissionProbe();
        var viewModel = new FixtureViewModel();
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> adapter =
            CreateAdapter(viewModel);
        MvvmBindingResult accepted = await DispatchRawAsync(
            canonicalInput,
            adapter,
            probe,
            MvvmLimits.Default);
        True(accepted.Committed);
        Equal("codec-first", viewModel.Name!);
        Equal(1, probe.Invocations);

        byte[] invalid = Encoding.UTF8.GetBytes(
            $$$"""{"v":1,"kind":"setProperty","session":"{{{SessionId}}}","view":"{{{ViewId}}}","request":"{{{RequestId}}}","baseRevision":0,"capability":"{{{Capability}}}","payload":{"member":1,"value":"first","value":"secret-second"}}""");
        await ThrowsAsync<MvvmProtocolException>(
            async () => await DispatchRawAsync(invalid, adapter, probe, MvvmLimits.Default));
        Equal(1, probe.Invocations);
        Equal("codec-first", viewModel.Name!);
    }

    private static async Task CommunityToolkitObservabilitySecurityAsync()
    {
        const string secret = "identity-capability-payload-secret";
        await using CommunityToolkitMvvmBindingAdapter<FixtureViewModel> adapter =
            CreateAdapter(new FixtureViewModel());
        MvvmBindingResult rejected = await adapter.DispatchAsync(
            PropertyMutation(4, Json($"\"{secret}\"")),
            CancellationToken.None);

        False(rejected.Succeeded);
        False(rejected.Committed);
        True(rejected.Fault is not null);
        string safeDiagnostic = $"{rejected.Fault!.Code}:{rejected.Fault.Message}";
        False(safeDiagnostic.Contains(secret, StringComparison.Ordinal));
        False(safeDiagnostic.Contains(SessionId, StringComparison.Ordinal));
        False(safeDiagnostic.Contains(ViewId, StringComparison.Ordinal));
        False(safeDiagnostic.Contains(Capability, StringComparison.Ordinal));
        True(safeDiagnostic.Length <= 512);
        Equal(
            adapter.Metadata.Count,
            adapter.Metadata.Select(static metadata => metadata.MemberId).Distinct().Count());
    }

    private static JsonElement[] SnapshotMembers(MvvmSnapshot snapshot) =>
        snapshot.State.GetProperty("members").EnumerateArray().ToArray();

    private static string MemberKey(JsonElement member) =>
        $"{member.GetProperty("member").GetInt32()}:{member.GetProperty("type").GetString()}";

    private static byte[] SetPropertyFrame(string valueJson) => Encoding.UTF8.GetBytes(
        $$$"""{"v":1,"kind":"setProperty","session":"{{{SessionId}}}","view":"{{{ViewId}}}","request":"{{{RequestId}}}","baseRevision":0,"capability":"{{{Capability}}}","payload":{"member":1,"value":{{{valueJson}}}}}""");

    private static byte[] AckFrame(long revision) => Encoding.UTF8.GetBytes(
        $$$"""{"v":1,"kind":"ack","session":"{{{SessionId}}}","view":"{{{ViewId}}}","request":"{{{RequestId}}}","capability":"{{{Capability}}}","payload":{"revision":{{{revision}}}}}""");

    private static byte[] HostSnapshotFrame(MvvmSnapshot snapshot)
    {
        string members = snapshot.State.GetProperty("members").GetRawText();
        return Encoding.UTF8.GetBytes(
            $$$"""{"v":1,"kind":"snapshot","session":"{{{SessionId}}}","view":"{{{ViewId}}}","request":"{{{RequestId}}}","payload":{"revision":0,"members":{{{members}}}}}""");
    }

    private static async Task<MvvmBindingResult> DispatchRawAsync(
        byte[] rawFrame,
        CommunityToolkitMvvmBindingAdapter<FixtureViewModel> adapter,
        AdmissionProbe probe,
        MvvmLimits limits)
    {
        MvvmWireMessage decoded = MvvmMessageCodec.DecodeClient(rawFrame, limits);
        JsonElement envelope = decoded.Document;
        JsonElement payload = envelope.GetProperty("payload");
        var request = new MvvmMutationRequest(
            new MvvmRequestId(envelope.GetProperty("request").GetGuid()),
            MvvmMutationKind.SetProperty,
            envelope.GetProperty("baseRevision").GetInt64(),
            payload.GetProperty("member").GetInt32(),
            payload.GetProperty("value"));
        probe.Invocations++;
        return await adapter.DispatchAsync(request, CancellationToken.None);
    }

    private sealed class AdmissionProbe
    {
        public int Invocations { get; set; }
    }
}
