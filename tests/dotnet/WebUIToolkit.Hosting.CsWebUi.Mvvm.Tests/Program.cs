using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.Hosting;
using WebUIToolkit.Hosting.CsWebUi.Mvvm;
using WebUIToolkit.MVVM;

namespace WebUIToolkit.Hosting.CsWebUi.Mvvm.Tests;

internal static class Program
{
    private const string View = "33333333-3333-4333-8333-333333333333";
    private const string Handshake =
        """{"v":1,"kind":"handshake","request":"00000000-0000-4000-8000-000000000001","payload":{"supportedVersions":[1],"capabilities":["cancellation","patches"]}}""";

    public static async Task<int> Main()
    {
        (string Name, Func<Task> Run)[] tests =
        [
            ("one binding carries handshake, open, mutation, cancel, and close", ProtocolRoundTrip),
            ("unsolicited session patches use the native window channel", HostPush),
            ("client identity is pinned while the same client may reconnect", ConnectionIdentity),
            ("invalid and oversized native calls are rejected before dispatch", InvalidFramesAreRejected),
            ("options require simple distinct JavaScript identifiers", OptionsValidate),
            ("frameworks extend the shared high-level builder", SharedBuilderExtensions),
        ];

        foreach ((string name, Func<Task> run) in tests)
        {
            try
            {
                await run().ConfigureAwait(false);
                Console.WriteLine($"ok - {name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"not ok - {name}");
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        return 0;
    }

    private static Task SharedBuilderExtensions()
    {
        var builder = WebUiApp.CreateBuilder();
        Equal("React", builder.React.Name);
        Equal("Vue", builder.Vue.Name);
        Equal("Svelte", builder.Svelte.Name);
        Equal("Angular", builder.Angular.Name);
        return Task.CompletedTask;
    }

    private static async Task HostPush()
    {
        await using TestSession test = await TestSession.CreateAsync().ConfigureAwait(false);
        var window = new FakeWindow();
        await using var bridge = CsWebUiMvvmBridge.Attach(window, test.Session);

        _ = await window.InvokeAsync(Bytes(Handshake)).ConfigureAwait(false);
        _ = await window.InvokeAsync(Bytes($$$"""
            {"v":1,"kind":"open","contract":"todo-test","view":"{{{View}}}","request":"00000000-0000-4000-8000-000000000002","payload":{}}
            """)).ConfigureAwait(false);

        test.ChangeFromHost("pushed");
        byte[] frame = await window.WaitForSentAsync().ConfigureAwait(false);
        JsonElement patch = Decode(frame);
        Equal("patch", patch.GetProperty("kind").GetString());
        JsonElement payload = patch.GetProperty("payload");
        Equal(0L, payload.GetProperty("fromRevision").GetInt64());
        Equal(1L, payload.GetProperty("toRevision").GetInt64());
        Equal(
            "pushed",
            payload.GetProperty("changes")[0]
                .GetProperty("value")
                .GetString());
    }

    private static async Task ProtocolRoundTrip()
    {
        await using TestSession test = await TestSession.CreateAsync().ConfigureAwait(false);
        var window = new FakeWindow();
        await using var bridge = CsWebUiMvvmBridge.Attach(window, test.Session);
        Equal(1, window.BindCount);
        Equal("__webuitoolkit_mvvm_send", window.BindingName);

        FakeEvent callback = await window.InvokeAsync(Bytes(Handshake)).ConfigureAwait(false);
        Equal("handshakeResult", Kind(callback.Sent.Single().Frame));
        Equal("__webuitoolkit_mvvm_receive", callback.Sent.Single().Function);

        callback = await window.InvokeAsync(Bytes($$$"""
            {"v":1,"kind":"open","contract":"todo-test","view":"{{{View}}}","request":"00000000-0000-4000-8000-000000000002","payload":{}}
            """)).ConfigureAwait(false);
        JsonElement opened = Decode(callback.Sent.Single().Frame);
        Equal("opened", opened.GetProperty("kind").GetString());
        Equal(0L, opened.GetProperty("payload").GetProperty("snapshot").GetProperty("revision").GetInt64());
        string session = opened.GetProperty("session").GetString()!;
        string capability = opened.GetProperty("capability").GetString()!;

        callback = await window.InvokeAsync(Bytes($$$"""
            {"v":1,"kind":"setProperty","session":"{{{session}}}","view":"{{{View}}}","request":"00000000-0000-4000-8000-000000000003","baseRevision":0,"capability":"{{{capability}}}","payload":{"member":1,"value":"changed"}}
            """)).ConfigureAwait(false);
        Equal(2, callback.Sent.Count);
        Equal("patch", Kind(callback.Sent[0].Frame));
        Equal("result", Kind(callback.Sent[1].Frame));
        Equal("changed", test.Value);

        callback = await window.InvokeAsync(Bytes($$$"""
            {"v":1,"kind":"cancel","session":"{{{session}}}","view":"{{{View}}}","request":"00000000-0000-4000-8000-000000000004","capability":"{{{capability}}}","payload":{"targetRequest":"00000000-0000-4000-8000-000000000003"}}
            """)).ConfigureAwait(false);
        JsonElement cancel = Decode(callback.Sent.Single().Frame);
        Equal("cancel", cancel.GetProperty("payload").GetProperty("operation").GetString());
        False(cancel.GetProperty("payload").GetProperty("accepted").GetBoolean());

        callback = await window.InvokeAsync(Bytes($$$"""
            {"v":1,"kind":"close","session":"{{{session}}}","view":"{{{View}}}","request":"00000000-0000-4000-8000-000000000005","capability":"{{{capability}}}","payload":{"reason":"test complete"}}
            """)).ConfigureAwait(false);
        Equal("closed", Kind(callback.Sent.Single().Frame));
        True(bridge.IsClosed);
        Equal(1, test.DisposeCount);
        Equal(1, window.BindingDisposeCount);
        await bridge.DisposeAsync().ConfigureAwait(false);
        Equal(1, test.DisposeCount);
    }

    private static async Task ConnectionIdentity()
    {
        await using TestSession test = await TestSession.CreateAsync().ConfigureAwait(false);
        var window = new FakeWindow();
        await using var bridge = CsWebUiMvvmBridge.Attach(window, test.Session);

        _ = await window.InvokeAsync(Bytes(Handshake), clientId: 7, connectionId: 11).ConfigureAwait(false);
        Equal(
            new CsWebUiMvvmConnectionIdentity(7, 11),
            bridge.ConnectionIdentity!.Value);

        FakeEvent rejected = await window
            .InvokeAsync(Bytes(Handshake), clientId: 8, connectionId: 12)
            .ConfigureAwait(false);
        True(rejected.ClientClosed);
        Equal(new CsWebUiMvvmConnectionIdentity(7, 11), bridge.ConnectionIdentity!.Value);

        FakeEvent reconnected = await window
            .InvokeAsync(Bytes(Handshake), clientId: 7, connectionId: 13)
            .ConfigureAwait(false);
        False(reconnected.ClientClosed);
        Equal("handshakeResult", Kind(reconnected.Sent.Single().Frame));
        Equal(new CsWebUiMvvmConnectionIdentity(7, 13), bridge.ConnectionIdentity!.Value);
    }

    private static async Task InvalidFramesAreRejected()
    {
        await using TestSession test = await TestSession.CreateAsync().ConfigureAwait(false);
        var window = new FakeWindow();
        await using var bridge = CsWebUiMvvmBridge.Attach(window, test.Session);

        FakeEvent malformed = await window.InvokeAsync([0xff]).ConfigureAwait(false);
        True(malformed.ClientClosed);
        Equal(0, test.DispatchCount);

        FakeEvent oversized = await window
            .InvokeAsync(new byte[MvvmLimits.MaximumPayloadBytes + 1])
            .ConfigureAwait(false);
        True(oversized.ClientClosed);
        Equal(0, test.DispatchCount);

        FakeEvent wrongArgumentCount = await window
            .InvokeAsync(Bytes(Handshake), argumentCount: 2)
            .ConfigureAwait(false);
        True(wrongArgumentCount.ClientClosed);
        Equal(0, test.DispatchCount);
    }

    private static Task OptionsValidate()
    {
        Throws<ArgumentException>(() =>
            CsWebUiMvvmBridge.Attach(
                new FakeWindow(),
                new NeverUsedSession(),
                new CsWebUiMvvmBridgeOptions { BindingName = "not.a.name" }));
        Throws<ArgumentException>(() =>
            CsWebUiMvvmBridge.Attach(
                new FakeWindow(),
                new NeverUsedSession(),
                new CsWebUiMvvmBridgeOptions
                {
                    BindingName = "same",
                    ReceiveFunctionName = "same",
                }));
        return Task.CompletedTask;
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static JsonElement Decode(byte[] frame) =>
        MvvmMessageCodec.DecodeHost(frame).Document;

    private static string? Kind(byte[] frame) => Decode(frame).GetProperty("kind").GetString();

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static void False(bool value) => True(!value);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
        }
    }

    private static void Throws<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

internal sealed class FakeWindow : ICsWebUiMvvmWindow
{
    private Func<ICsWebUiMvvmEvent, CancellationToken, ValueTask>? _callback;
    private readonly object _sendGate = new();

    internal int BindCount { get; private set; }

    internal string? BindingName { get; private set; }

    internal int BindingDisposeCount { get; private set; }

    internal List<(string Function, byte[] Frame)> Sent { get; } = [];

    public IDisposable Bind(
        string name,
        Func<ICsWebUiMvvmEvent, CancellationToken, ValueTask> callback)
    {
        BindCount++;
        BindingName = name;
        _callback = callback;
        return new CallbackDisposable(() =>
        {
            BindingDisposeCount++;
            _callback = null;
        });
    }

    public void SendRaw(string functionName, ReadOnlySpan<byte> data)
    {
        lock (_sendGate)
        {
            Sent.Add((functionName, data.ToArray()));
        }
    }

    internal async Task<byte[]> WaitForSentAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            lock (_sendGate)
            {
                if (Sent.Count != 0)
                {
                    return Sent[0].Frame;
                }
            }

            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }

        throw new TimeoutException("No native window frame was sent.");
    }

    internal async Task<FakeEvent> InvokeAsync(
        byte[] frame,
        ulong clientId = 7,
        ulong connectionId = 11,
        int argumentCount = 1)
    {
        var webUiEvent = new FakeEvent(frame, clientId, connectionId, argumentCount);
        Func<ICsWebUiMvvmEvent, CancellationToken, ValueTask> callback =
            _callback ?? throw new InvalidOperationException("No binding is active.");
        await callback(webUiEvent, CancellationToken.None).ConfigureAwait(false);
        return webUiEvent;
    }
}

internal sealed class FakeEvent(
    byte[] frame,
    ulong clientId,
    ulong connectionId,
    int argumentCount) : ICsWebUiMvvmEvent
{
    public ulong ClientId { get; } = clientId;

    public ulong ConnectionId { get; } = connectionId;

    public int ArgumentCount { get; } = argumentCount;

    internal List<(string Function, byte[] Frame)> Sent { get; } = [];

    internal bool ClientClosed { get; private set; }

    public byte[] GetBytes(int index) => frame.ToArray();

    public void SendRaw(string functionName, ReadOnlySpan<byte> data) =>
        Sent.Add((functionName, data.ToArray()));

    public void CloseClient() => ClientClosed = true;
}

internal sealed class CallbackDisposable(Action callback) : IDisposable
{
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            callback();
        }
    }
}

internal sealed class TestSession : IAsyncDisposable
{
    private readonly IMvvmSessionFactory _factory;
    private readonly ChangeSourceAdapter _adapter;
    private int _disposeCount;
    private int _dispatchCount;

    private TestSession(
        IMvvmSessionFactory factory,
        IMvvmSession session,
        ChangeSourceAdapter adapter)
    {
        _factory = factory;
        _adapter = adapter;
        Session = new TrackingSession(
            session,
            () => Interlocked.Increment(ref _disposeCount),
            () => Interlocked.Increment(ref _dispatchCount));
    }

    internal IMvvmSession Session { get; }

    internal string Value { get; private set; } = "initial";

    internal int DisposeCount => Volatile.Read(ref _disposeCount);

    internal int DispatchCount => Volatile.Read(ref _dispatchCount);

    internal void ChangeFromHost(string value)
    {
        Value = value;
        _adapter.RaiseChanged();
    }

    internal static async Task<TestSession> CreateAsync()
    {
        var vocabulary = new MvvmBindingVocabulary(
        [
            new MvvmBindingMember(1, MvvmBindingMemberKind.Property),
        ]);
        var registry = new MvvmSessionRegistry();
        var contract = new MvvmContract("todo-test");
        TestSession? owner = null;
        ChangeSourceAdapter? changeSource = null;
        registry.Map(contract, _ =>
        {
            IMvvmBindingAdapter adapter = new MvvmBindingAdapterBuilder(
                    cancellationToken =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return ValueTask.FromResult(
                            new MvvmProjectionSnapshotBuilder(vocabulary)
                                .AddProperty(
                                    1,
                                    JsonSerializer.SerializeToElement(owner?.Value ?? "initial"))
                                .Build());
                    },
                    vocabulary)
                .BindProperty(1, (request, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    owner!.Value = request.Payload.GetString()!;
                    return ValueTask.FromResult(
                        MvvmBindingResult.Success(
                            patches: [new MvvmPropertyPatch(1, request.Payload)]));
                })
                .Build();
            changeSource = new ChangeSourceAdapter(
                adapter,
                () => JsonSerializer.SerializeToElement(owner?.Value ?? "initial"));
            return ValueTask.FromResult<MvvmSessionActivation>(new(changeSource));
        });
        IMvvmSessionFactory factory = registry.Build();
        IMvvmSession session = await factory.OpenAsync(contract).ConfigureAwait(false);
        owner = new TestSession(factory, session, changeSource!);
        return owner;
    }

    public async ValueTask DisposeAsync() =>
        await _factory.DisposeAsync().ConfigureAwait(false);
}

internal sealed class TrackingSession(
    IMvvmSession inner,
    Action disposed,
    Action dispatched) : IMvvmSession
{
    public MvvmSessionId Id => inner.Id;

    public MvvmContract Contract => inner.Contract;

    public string CapabilityToken => inner.CapabilityToken;

    public long Revision => inner.Revision;

    public long? AcknowledgedRevision => inner.AcknowledgedRevision;

    public event EventHandler<MvvmProjectionChangedEventArgs>? ProjectionChanged
    {
        add => inner.ProjectionChanged += value;
        remove => inner.ProjectionChanged -= value;
    }

    public bool Authorizes(string capabilityToken) => inner.Authorizes(capabilityToken);

    public async ValueTask<MvvmResponse> DispatchAsync(
        MvvmRequest request,
        CancellationToken cancellationToken = default)
    {
        dispatched();
        return await inner.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        disposed();
        await inner.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class ChangeSourceAdapter(
    IMvvmBindingAdapter inner,
    Func<JsonElement> value) : IMvvmBindingAdapter, IMvvmBindingChangeSource
{
    public event EventHandler? StateChanged;

    internal void RaiseChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public ValueTask<MvvmSnapshot> SnapshotAsync(CancellationToken cancellationToken) =>
        inner.SnapshotAsync(cancellationToken);

    public ValueTask<MvvmBindingResult> DispatchAsync(
        MvvmMutationRequest request,
        CancellationToken cancellationToken) =>
        inner.DispatchAsync(request, cancellationToken);

    public ValueTask<IReadOnlyList<MvvmPatch>> ProjectChangesAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<MvvmPatch>>(
            [new MvvmPropertyPatch(1, value())]);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

internal sealed class NeverUsedSession : IMvvmSession
{
    public MvvmSessionId Id => new(new Guid("11111111-1111-4111-8111-111111111111"));

    public MvvmContract Contract => new("unused");

    public string CapabilityToken => "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    public long Revision => 0;

    public long? AcknowledgedRevision => null;

    public bool Authorizes(string capabilityToken) => false;

    public ValueTask<MvvmResponse> DispatchAsync(
        MvvmRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
