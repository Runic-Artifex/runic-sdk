using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WebUIToolkit.MVVM;

namespace WebUIToolkit.MVVM.AotSmoke;

internal static class Program
{
    private const int CountMember = 1;
    private const int IncrementCommand = 2;
    private static readonly MvvmBindingVocabulary Vocabulary = new(
        [
            new MvvmBindingMember(CountMember, MvvmBindingMemberKind.Property, "Count"),
            new MvvmBindingMember(IncrementCommand, MvvmBindingMemberKind.Command, "Increment"),
        ]);

    public static async Task<int> Main()
    {
        if (!ExerciseCodec())
        {
            return 1;
        }

        var state = new CounterState();
        int subscriptionCleanupCalls = 0;
        var registry = new MvvmSessionRegistry();
        registry.Map(
            new MvvmContract("aot-smoke"),
            _ => ValueTask.FromResult(new MvvmSessionActivation(CreateAdapter(
                state,
                () => subscriptionCleanupCalls++))));

        await using IMvvmSessionFactory factory = registry.Build();
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("aot-smoke"));

        if (!session.Authorizes(session.CapabilityToken) || session.Authorizes("not-a-capability"))
        {
            return Fail("Capability authorization failed.");
        }

        MvvmResponse initialSnapshot = await session.DispatchAsync(new MvvmSnapshotRequest(NewId()));
        var mutationRequest = new MvvmMutationRequest(
            NewId(),
            MvvmMutationKind.ExecuteCommand,
            baseRevision: 0,
            memberId: IncrementCommand,
            payload: MvvmValue.Null);
        if (!Vocabulary.TryResolve(mutationRequest, out MvvmBindingMember? binding) ||
            binding.Kind != MvvmBindingMemberKind.Command || Vocabulary.Members.Count != 2)
        {
            return Fail("Binding vocabulary resolution failed.");
        }

        MvvmResponse mutation = await session.DispatchAsync(mutationRequest);
        MvvmResponse acknowledgement = await session.DispatchAsync(new MvvmAcknowledgeRequest(NewId(), revision: 1));
        MvvmResponse reconnectSnapshot = await session.DispatchAsync(new MvvmSnapshotRequest(NewId()));

        if (!SnapshotHasCount(initialSnapshot, expectedRevision: 0, expectedCount: 0) ||
            !mutation.Succeeded || mutation.Revision != 1 || mutation.Payload?.GetInt64() != 1 ||
            mutation.Patches.Count != 2 || mutation.Patches[0] is not MvvmPropertyPatch propertyPatch ||
            propertyPatch.MemberId != CountMember || propertyPatch.Value.GetInt64() != 1 ||
            mutation.Patches[1] is not MvvmCommandPatch commandPatch ||
            commandPatch.MemberId != IncrementCommand || !commandPatch.CanExecute || commandPatch.IsExecuting ||
            !acknowledgement.Succeeded || acknowledgement.Revision != 1 ||
            session.AcknowledgedRevision != 1 || session.Revision != 1 ||
            !SnapshotHasCount(reconnectSnapshot, expectedRevision: 1, expectedCount: 1))
        {
            return Fail("Session, projection, binding, acknowledgement, or reconnect smoke failed.");
        }

        await session.DisposeAsync();
        if (subscriptionCleanupCalls != 1)
        {
            return Fail("Binding subscription cleanup did not run exactly once.");
        }

        Console.WriteLine($"{MvvmProtocol.Identity} package/Native-AOT smoke passed at revision {session.Revision}.");
        return 0;
    }

    private static bool ExerciseCodec()
    {
        byte[] clientFrame = Encoding.UTF8.GetBytes(
            "{\"v\":1,\"kind\":\"execute\",\"session\":\"00000000-0000-4000-8000-000000000004\",\"view\":\"00000000-0000-4000-8000-000000000002\",\"request\":\"00000000-0000-4000-8000-000000000006\",\"baseRevision\":0,\"capability\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"payload\":{\"member\":2,\"argument\":null}}");
        byte[] hostFrame = Encoding.UTF8.GetBytes(
            "{\"v\":1,\"kind\":\"result\",\"session\":\"00000000-0000-4000-8000-000000000004\",\"view\":\"00000000-0000-4000-8000-000000000002\",\"request\":\"00000000-0000-4000-8000-000000000006\",\"payload\":{\"operation\":\"execute\",\"revision\":1,\"value\":1}}");

        MvvmWireMessage client = MvvmMessageCodec.DecodeClient(clientFrame);
        MvvmWireMessage host = MvvmMessageCodec.DecodeHost(hostFrame);
        byte[] encoded = MvvmMessageCodec.Encode(client);
        byte[] encodedAgain = MvvmMessageCodec.Encode(MvvmMessageCodec.DecodeClient(encoded));
        bool rejectedInvalidUtf8 = !MvvmMessageCodec.TryDecodeClient(
            [0xc3, 0x28],
            out _,
            out MvvmProtocolException? error) &&
            error?.Code == MvvmValidationErrorCodes.InvalidUtf8;

        return client.Direction == MvvmMessageDirection.ClientToHost && client.Kind == "execute" &&
            host.Direction == MvvmMessageDirection.HostToClient && host.Kind == "result" &&
            encoded.AsSpan().SequenceEqual(encodedAgain) && rejectedInvalidUtf8;
    }

    private static IMvvmBindingAdapter CreateAdapter(CounterState state, Action subscriptionCleanup)
    {
        return new MvvmBindingAdapterBuilder(
            _ => ValueTask.FromResult(CreateSnapshot(state.Count)),
            Vocabulary)
            .BindCommand(
                IncrementCommand,
                (_, _) =>
                {
                    state.Count++;
                    return ValueTask.FromResult(
                        new MvvmProjectionPatchBuilder(Vocabulary)
                            .Property(CountMember, MvvmValue.From((long)state.Count))
                            .Command(IncrementCommand, canExecute: true, isExecuting: false)
                            .Success(MvvmValue.From((long)state.Count)));
                },
                diagnosticName: "Increment")
            .OnDispose(() =>
            {
                subscriptionCleanup();
                return ValueTask.CompletedTask;
            })
            .Build();
    }

    private static MvvmSnapshot CreateSnapshot(int count) =>
        new MvvmProjectionSnapshotBuilder(Vocabulary)
            .AddProperty(CountMember, MvvmValue.From((long)count))
            .AddCommand(IncrementCommand, canExecute: true, isExecuting: false)
            .Build();

    private static bool SnapshotHasCount(MvvmResponse response, long expectedRevision, long expectedCount)
    {
        if (!response.Succeeded || response.Revision != expectedRevision || response.Payload is not JsonElement payload ||
            !payload.TryGetProperty("members", out JsonElement members) || members.GetArrayLength() != 2)
        {
            return false;
        }

        JsonElement property = members[0];
        JsonElement command = members[1];
        return property.GetProperty("type").GetString() == "property" &&
            property.GetProperty("member").GetInt32() == CountMember &&
            property.GetProperty("value").GetInt64() == expectedCount &&
            command.GetProperty("type").GetString() == "command" &&
            command.GetProperty("member").GetInt32() == IncrementCommand;
    }

    private static MvvmRequestId NewId() => new(Guid.NewGuid());

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private sealed class CounterState
    {
        internal int Count { get; set; }
    }
}
