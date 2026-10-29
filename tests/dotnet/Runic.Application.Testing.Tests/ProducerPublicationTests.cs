using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Runic.Application.Views;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Producer performance (0.14): what a publication unit encodes, how often notification units run, the subscription
// graph's sweeps, the pooled buffers and the drain's model turns. tests/benchmarks measures the same paths.
internal sealed class ProducerPublicationTests
{
    // A set's reply publishes the change; the unit its notification requested finds nothing new and encodes nothing.
    [Test]
    public async Task ASetEncodesTheStateOnceAsync()
    {
        using var page = new RecordingTransport();
        var model = new Model();
        using var bridge = new ModelBridge(page, model);
        _ = page.Request("""{"kind":"snapshot","route":"model"}""");
        await Task.Delay(50);
        var before = model.ArchiveWrites;
        using (var reply = JsonDocument.Parse(page.Request("""{"kind":"set","route":"model","member":"title","value":"a","edit":1}""")))
            Require(reply.RootElement.GetProperty("ok").GetBoolean(), $"The set failed: {reply.RootElement}");
        // Longer than the minimum unit interval, so a requested unit would have run.
        await Task.Delay(100);
        Require(model.ArchiveWrites - before == 1, $"A set encoded the unchanged archive {model.ArchiveWrites - before} times.");
        Require(page.Frames.Count(frame => frame.Contains("\"title\"", StringComparison.Ordinal)) == 1,
            $"The set did not publish one frame: [{string.Join(", ", page.Frames)}]");
    }

    // Notifications faster than the minimum interval coalesce: the first unit runs at once, later ones wait for the
    // rest of the interval, and the last change is always published.
    [Test]
    public async Task NotificationUnitsKeepTheMinimumIntervalAsync()
    {
        using var page = new RecordingTransport();
        BridgeDispatcher.For(page).MinimumUnitInterval = TimeSpan.FromMilliseconds(200);
        var model = new Model();
        using var bridge = new ModelBridge(page, model);
        _ = page.Request("""{"kind":"snapshot","route":"model"}""");
        await Task.Delay(300);
        var before = page.Frames.Length;
        model.Counter = 1;
        await page.WaitForAsync("\"value\":1}", TimeSpan.FromSeconds(5));
        for (var value = 2; value <= 20; value++) model.Counter = value;
        await page.WaitForAsync("\"value\":20}", TimeSpan.FromSeconds(5));
        var frames = page.Frames.Length - before;
        Require(frames == 2, $"20 changes within the interval published {frames} frames instead of the first and the last.");
    }

    // A reply publishes at once, whatever the interval, and takes along the change of a unit that waits for it.
    [Test]
    public async Task AReplyDoesNotWaitForTheMinimumIntervalAsync()
    {
        using var page = new RecordingTransport();
        var dispatcher = BridgeDispatcher.For(page);
        dispatcher.MinimumUnitInterval = TimeSpan.Zero;
        var model = new Model();
        using var bridge = new ModelBridge(page, model);
        _ = page.Request("""{"kind":"snapshot","route":"model"}""");
        // A unit that just ran makes the next one wait for the interval.
        model.Counter = 1;
        await page.WaitForAsync("\"value\":1}", TimeSpan.FromSeconds(5));
        dispatcher.MinimumUnitInterval = TimeSpan.FromSeconds(10);
        model.Counter = 2;
        await Task.Delay(50);
        Require(!page.Frames.Any(frame => frame.Contains("\"value\":2}", StringComparison.Ordinal)), "A unit did not wait for the interval.");
        var started = Stopwatch.GetTimestamp();
        _ = await page.RequestAsync("""{"kind":"set","route":"model","member":"title","value":"echo","edit":1}""");
        var elapsed = Stopwatch.GetElapsedTime(started);
        Require(elapsed < TimeSpan.FromSeconds(5), $"A set waited {elapsed} for the minimum unit interval.");
        Require(page.Frames.Any(frame => frame.Contains("\"echo\"", StringComparison.Ordinal) && frame.Contains("\"value\":2}", StringComparison.Ordinal)),
            $"The set's frame with the waiting change was not delivered before its reply: [{string.Join(", ", page.Frames)}]");
    }

    // The route's first state, captured at attach, is the state of an open until the route changes.
    [Test]
    public async Task AnOpenAfterAttachCopiesTheFirstStateAsync()
    {
        using var page = new RecordingTransport();
        var model = new Model();
        using var bridge = new ModelBridge(page, model);
        await WaitUntilAsync(() => model.ArchiveWrites == 1, "The first state was not captured.");
        using (var open = JsonDocument.Parse(page.Request("""{"kind":"open","route":"model","presentation":"p1"}""")))
            Require(open.RootElement.GetProperty("state").GetProperty("archive").GetArrayLength() == 3, $"The open has no state: {open.RootElement}");
        Require(model.ArchiveWrites == 1, $"The open encoded the state again ({model.ArchiveWrites} writes).");
        model.Counter = 5;
        await page.WaitForAsync("\"value\":5}", TimeSpan.FromSeconds(5));
        using (var open = JsonDocument.Parse(page.Request("""{"kind":"open","route":"model","presentation":"p2"}""")))
            Require(open.RootElement.GetProperty("state").GetProperty("counter").GetInt32() == 5, $"The second open is stale: {open.RootElement}");
    }

    // Adding rows attaches their nodes without walking the subscription graph; a removal releases the removed row by
    // the end of the unit, also without a walk unless a detached cycle may remain.
    [Test]
    public void AddsNeverWalkTheSubscriptionGraph()
    {
        var root = new Rows();
        var rowMembers = new[] { new BridgeDataSubscriptionMember("title", static owner => ((Row)owner).Title) };
        var members = new[]
        {
            new BridgeDataSubscriptionMember("items", static owner => ((Rows)owner).Items, rowMembers, static value => (IEnumerable)value),
        };
        var notifications = 0;
        using var subscriptions = new BridgeDataSubscriptions(root, members, () => notifications++);
        for (var index = 0; index < 100; index++) root.Items.Add(new Row());
        subscriptions.SweepPending();
        var removed = root.Items[0];
        root.Items.RemoveAt(0);
        subscriptions.SweepPending();
        Require(subscriptions.Walks == 0, $"Adds and a removal walked the graph {subscriptions.Walks} times.");
        var seen = notifications;
        removed.Title = "after removal";
        Require(notifications == seen, "A removed row stayed subscribed.");
        root.Items[0].Title = "retained";
        Require(notifications == seen + 1, "A retained row was not subscribed.");
    }

    // A unit's drain awaits the model context's turn: routes whose context is busy hold no thread-pool thread, so the
    // pool does not grow for them.
    [Test]
    public async Task DrainsAwaitTheModelTurnAsync()
    {
        const int Routes = 64;
        await using var inner = new RunicModelContext();
        var context = new CountingContext(inner);
        using var page = new RecordingTransport();
        BridgeDispatcher.For(page).MinimumUnitInterval = TimeSpan.Zero;
        var models = Enumerable.Range(0, Routes).Select(_ => new Model()).ToArray();
        using var ownership = RunicModelContextRegistry.Shared.Bind(context, [.. models]);
        var bridges = models.Select((model, index) => new ModelBridge(page, model, $"route{index.ToString(CultureInfo.InvariantCulture)}")).ToArray();
        using var release = new ManualResetEventSlim();
        try
        {
            for (var index = 0; index < Routes; index++)
                _ = page.Request($$"""{"kind":"snapshot","route":"route{{index.ToString(CultureInfo.InvariantCulture)}}"}""");
            var invoked = context.Invocations;
            var threads = ThreadPool.ThreadCount;
            // One long turn changes every route and then holds the context while each route's drain asks for a turn.
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var turn = Task.Run(() => inner.InvokeAsync(() =>
            {
                foreach (var model in models) model.Counter++;
                changed.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10));
            }).AsTask());
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await WaitUntilAsync(() => context.Invocations - invoked >= Routes,
                    $"Only {context.Invocations - invoked} of {Routes} drains asked for a turn while the context was busy.",
                    TimeSpan.FromSeconds(5));
                // A drain that blocked for its turn held a thread-pool thread, and the pool added threads for them.
                var added = ThreadPool.ThreadCount - threads;
                Require(added < 8, $"{Routes} drains waiting for a busy model context added {added} thread-pool threads.");
            }
            finally { release.Set(); }
            await turn.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => page.Frames.Count(frame => frame.Contains("\"value\":1}", StringComparison.Ordinal)) == Routes,
                "Not every route published its change.");
        }
        finally
        {
            release.Set();
            foreach (var bridge in bridges) bridge.Dispose();
        }
    }

    [Test]
    public void PooledWritersCompareAndCopyAcrossSegments()
    {
        var bytes = new byte[20_000];
        for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index % 251);
        using var pooled = new BridgePooledWriter();
        // Writes in pieces, so the content spans the 4 KiB, 8 KiB and 16 KiB segments.
        for (var offset = 0; offset < bytes.Length; offset += 1000) pooled.Write(bytes.AsSpan(offset, 1000));
        Require(pooled.Length == bytes.Length && pooled.ToArray().AsSpan().SequenceEqual(bytes), "The segments lost bytes.");
        Require(pooled.SequenceEqual(3_000, bytes.AsSpan(3_000, 10_000)), "A range across segments did not compare equal.");
        var changed = bytes.AsSpan(3_000, 10_000).ToArray();
        changed[^1] ^= 1;
        Require(!pooled.SequenceEqual(3_000, changed), "A different range compared equal.");
        Require(pooled.ToArray(4_090, 20).AsSpan().SequenceEqual(bytes.AsSpan(4_090, 20)), "A copy across a segment boundary differs.");
        var message = pooled.Detach();
        try { Require(message.Memory.Span.SequenceEqual(bytes) && pooled.Length == 0, "A detached message differs."); }
        finally { message.Release(); }
    }

    [Test]
    public void ComparingWritersKeepOnlyChangedBytes()
    {
        var baseline = Encode(writer => WriteStrings(writer, 2_000, "row"));
        Require(Compare(baseline, writer => WriteStrings(writer, 2_000, "row")) is null, "An equal value did not match its baseline.");
        foreach (var (description, write) in new (string, Action<Utf8JsonWriter>)[]
        {
            ("changed at its end", writer => WriteStrings(writer, 2_000, "row", last: "changed")),
            ("shorter", writer => WriteStrings(writer, 1_999, "row")),
            ("longer", writer => WriteStrings(writer, 2_001, "row")),
            ("changed at its start", writer => WriteStrings(writer, 2_000, "Row")),
        })
        {
            var expected = Encode(write);
            var actual = Compare(baseline, write);
            Require(actual is not null && actual.AsSpan().SequenceEqual(expected), $"A value {description} did not keep its own bytes.");
        }
        Require(Compare(null, writer => writer.WriteNumberValue(1)) is [(byte)'1'], "A value without a baseline was not kept.");
    }

    private static byte[]? Compare(byte[]? baseline, Action<Utf8JsonWriter> write)
    {
        var comparing = BridgeComparingWriter.Rent(baseline);
        try
        {
            write(comparing.Writer);
            return comparing.Matches ? null : comparing.ToArray();
        }
        finally { comparing.Dispose(); }
    }

    private static byte[] Encode(Action<Utf8JsonWriter> write)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, BridgeJsonEncoder.WriterOptions)) write(writer);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteStrings(Utf8JsonWriter writer, int count, string prefix, string? last = null)
    {
        writer.WriteStartArray();
        for (var index = 0; index < count; index++)
            writer.WriteStringValue(index == count - 1 && last is not null ? last : $"{prefix} {index.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteEndArray();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure, TimeSpan? limit = null)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) > (limit ?? TimeSpan.FromSeconds(5))) throw new TimeoutException(failure);
            await Task.Delay(5);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Model : INotifyPropertyChanged
    {
        private string _title = "";
        private int _counter;
        private int _archiveWrites;

        public string Title
        {
            get => _title;
            set { _title = value; PropertyChanged?.Invoke(this, new(nameof(Title))); }
        }

        public int Counter
        {
            get => _counter;
            set { _counter = value; PropertyChanged?.Invoke(this, new(nameof(Counter))); }
        }

        public string[] Archive { get; } = ["first", "second", "third"];

        // How often the archive member was encoded.
        public int ArchiveWrites => Volatile.Read(ref _archiveWrites);

        public void WroteArchive() => Interlocked.Increment(ref _archiveWrites);

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class ModelBridge(IBridgeTransport transport, Model model, string route = "model") : ViewModelBridge<Model>(
        transport, model, route,
        [
            new BridgeStateMember<Model>("archive", static (writer, vm) =>
            {
                vm.WroteArchive();
                writer.WriteStartArray();
                foreach (var item in vm.Archive) writer.WriteStringValue(item);
                writer.WriteEndArray();
            }),
            new BridgeStateMember<Model>("counter", static (writer, vm) => writer.WriteNumberValue(vm.Counter)),
            new BridgeStateMember<Model>("title", static (writer, vm) => writer.WriteStringValue(vm.Title)),
        ],
        [new("Title", "title", vm => vm.Title, (vm, value) => vm.Title = BridgeWire.ReadString(value))], [], []);

    private sealed class Row : INotifyPropertyChanged
    {
        private string _title = "";

        public string Title
        {
            get => _title;
            set { _title = value; PropertyChanged?.Invoke(this, new(nameof(Title))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class Rows : INotifyPropertyChanged
    {
        public ObservableCollection<Row> Items { get; } = [];
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    // Records each push as text and lets a test wait for one that contains a marker.
    private sealed class RecordingTransport : PushTestTransport
    {
        private readonly List<string> _frames = [];
        public string[] Frames { get { lock (_frames) return [.. _frames]; } }

        protected override void OnBroadcast(string json)
        {
            lock (_frames) _frames.Add(json);
        }

        public async Task WaitForAsync(string marker, TimeSpan limit)
        {
            var started = Stopwatch.GetTimestamp();
            while (!Frames.Any(frame => frame.Contains(marker, StringComparison.Ordinal)))
            {
                if (Stopwatch.GetElapsedTime(started) > limit)
                    throw new TimeoutException($"No push with {marker} within {limit}: [{string.Join(", ", Frames)}]");
                await Task.Delay(2);
            }
        }
    }

    // A model context that counts the turns asked of it.
    private sealed class CountingContext(RunicModelContext inner) : IRunicModelContext
    {
        private int _invocations;
        public int Invocations => Volatile.Read(ref _invocations);
        public bool IsExecuting => inner.IsExecuting;
        public event EventHandler<RunicModelTurnExceptionEventArgs>? UnhandledTurnException
        {
            add => inner.UnhandledTurnException += value;
            remove => inner.UnhandledTurnException -= value;
        }
        public bool TryPost(Action turn) => inner.TryPost(turn);
        public ValueTask InvokeAsync(Action turn, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocations);
            return inner.InvokeAsync(turn, cancellationToken);
        }
        public ValueTask<T> InvokeAsync<T>(Func<T> turn, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocations);
            return inner.InvokeAsync(turn, cancellationToken);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
