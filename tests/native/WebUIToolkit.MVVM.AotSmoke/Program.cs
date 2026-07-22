using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.MVVM;

namespace WebUIToolkit.MVVM.AotSmoke;

internal static class Program
{
    public static async Task<int> Main()
    {
        var registry = new MvvmSessionRegistry();
        registry.Map(
            new MvvmContract("aot-smoke"),
            _ => ValueTask.FromResult(new MvvmSessionActivation(new CounterAdapter())));

        await using IMvvmSessionFactory factory = registry.Build();
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("aot-smoke"));

        MvvmResponse snapshot = await session.DispatchAsync(new MvvmSnapshotRequest(NewId()));
        MvvmResponse mutation = await session.DispatchAsync(
            new MvvmMutationRequest(NewId(), MvvmMutationKind.ExecuteCommand, 0, 1, Json("null")));

        if (!snapshot.Succeeded || snapshot.Revision != 0 ||
            snapshot.Payload?.GetProperty("count").GetInt32() != 0 ||
            !mutation.Succeeded || mutation.Revision != 1 || session.Revision != 1)
        {
            Console.Error.WriteLine("Native-AOT runtime round trip failed.");
            return 1;
        }

        Console.WriteLine($"{MvvmProtocol.Identity} Native-AOT runtime round trip passed at revision {session.Revision}.");
        return 0;
    }

    private static MvvmRequestId NewId() => new(Guid.NewGuid());

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class CounterAdapter : IMvvmBindingAdapter
    {
        private int _count;

        public ValueTask<MvvmSnapshot> SnapshotAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MvvmSnapshot(Json($"{{\"count\":{_count}}}")));

        public ValueTask<MvvmBindingResult> DispatchAsync(
            MvvmMutationRequest request,
            CancellationToken cancellationToken)
        {
            _count++;
            return ValueTask.FromResult(MvvmBindingResult.Success(
                Json(_count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                [new MvvmPropertyPatch(1, Json(_count.ToString(System.Globalization.CultureInfo.InvariantCulture)))]));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
