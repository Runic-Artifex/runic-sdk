using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Bridge;

namespace Runic.Application.Bridge.Tests;

internal static class WindowBridgeCheckedTitleFieldTests
{
    internal static async Task RunAsync()
    {
        await OneTitleOwnerServesTwoRoutes().ConfigureAwait(false);
        await DirectWriteWaitsForCheckedWriteGate().ConfigureAwait(false);
        SetterFailureRetainsTruthfulTerminalReceipt();
        ReceiptsAreBoundedAndCannotReplayAfterEviction();
    }

    private static async Task OneTitleOwnerServesTwoRoutes()
    {
        var transport = new RecordingTransport();
        await using var window = new WindowBridgeSession(transport);
        var notes = new Notes { Title = "Draft" };
        var title = new WindowBridgeCheckedTitleField(() => notes.Title, value => notes.Title = value);
        WindowBridgeReference left = window.Expose("notes-shell", notes, Attach);
        WindowBridgeReference right = window.Expose("notes-editor", notes, Attach);
        using WindowBridgePresentationLease leftView = window.Mount(left, WindowBridgeConnection.Create("client", "left"), "shell");
        using WindowBridgePresentationLease rightView = window.Mount(right, WindowBridgeConnection.Create("client", "right"), "editor");

        WindowBridgeTitleBaseline staleBaseline = WindowBridgeTitleBaseline.From(title.Snapshot());
        WindowBridgeTitleReceipt direct = title.Set("shell-1", "From shell");
        Equal(WindowBridgeTitleWriteKind.Applied, direct.Kind, "The shell route did not receive an applied title receipt.");
        Equal("From shell", direct.Current.Value, "The direct title receipt did not carry the model value.");
        Equal(1L, direct.Current.Version, "The direct title receipt did not advance the shared model version.");

        WindowBridgeTitleReceipt conflict = title.WriteChecked("editor-stale", staleBaseline, "From editor");
        Equal(WindowBridgeTitleWriteKind.Conflict, conflict.Kind, "A stale editor baseline overwrote the shell title.");
        Equal("From shell", conflict.Current.Value, "Conflict did not return the authoritative title snapshot.");
        Equal(1L, conflict.Current.Version, "Conflict did not return the authoritative title version.");

        WindowBridgeTitleReceipt checkedWrite = title.WriteChecked("editor-1", WindowBridgeTitleBaseline.From(conflict.Current), "From editor");
        Equal(WindowBridgeTitleWriteKind.Applied, checkedWrite.Kind, "The reconciled editor write was not applied.");
        Equal("From editor", notes.Title, "Two routes did not share the same Notes title model.");
        Equal(2L, checkedWrite.Current.Version, "The reconciled editor write did not retain the shared version sequence.");

        WindowBridgeTitleReceipt duplicate = title.Set("shell-1", "From shell");
        Equal(direct, duplicate, "An identical request did not replay its stable typed receipt.");
        WindowBridgeTitleReceipt reused = title.Set("shell-1", "Different value");
        Equal(WindowBridgeTitleWriteKind.Rejected, reused.Kind, "A request identifier was reused for a different title input.");
        Equal("From editor", notes.Title, "A rejected duplicate request mutated the shared model.");

        WindowBridgeTitleReceipt cancelled = await Task.Run(() => title.WriteChecked("editor-2", WindowBridgeTitleBaseline.From(checkedWrite.Current), "Concurrent editor")).ConfigureAwait(false);
        Equal(WindowBridgeTitleWriteKind.Applied, cancelled.Kind, "The field owner did not serialize a valid follow-up checked write.");

        WindowBridgeTitleBaseline concurrentBaseline = WindowBridgeTitleBaseline.From(title.Snapshot());
        using var start = new Barrier(2);
        Task<WindowBridgeTitleReceipt> concurrentDirect = Task.Run(() =>
        {
            start.SignalAndWait();
            return title.Set("shell-concurrent", "Shell concurrent");
        });
        Task<WindowBridgeTitleReceipt> concurrentChecked = Task.Run(() =>
        {
            start.SignalAndWait();
            return title.WriteChecked("editor-concurrent", concurrentBaseline, "Editor concurrent");
        });
        WindowBridgeTitleReceipt[] concurrent = await Task.WhenAll(concurrentDirect, concurrentChecked).ConfigureAwait(false);
        int applied = (concurrent[0].Kind == WindowBridgeTitleWriteKind.Applied ? 1 : 0)
            + (concurrent[1].Kind == WindowBridgeTitleWriteKind.Applied ? 1 : 0);
        True(applied is 1 or 2, "Direct and checked title writes did not produce serialized terminal receipts.");
        Equal(3L + applied, title.Snapshot().Version, "Concurrent direct and checked writes lost or duplicated a shared model turn.");
    }

    private static void ReceiptsAreBoundedAndCannotReplayAfterEviction()
    {
        var notes = new Notes { Title = "Draft" };
        var title = new WindowBridgeCheckedTitleField(() => notes.Title, value => notes.Title = value,
            maximumReceipts: 1, maximumExpired: 2, maximumRequestIdLength: 8, maximumTitleLength: 8);
        _ = title.Set("one", "One");
        _ = title.Set("two", "Two");
        Equal(WindowBridgeTitleWriteKind.Expired, title.Set("one", "Replay").Kind,
            "An evicted title receipt replayed model work.");
        Equal("Two", notes.Title, "An expired request changed the Notes title.");
        Equal(WindowBridgeTitleWriteKind.Rejected, title.Set("too-long-request", "Three").Kind,
            "An oversized title request entered the receipt table.");
        Equal(WindowBridgeTitleWriteKind.Rejected, title.Set("three", "123456789").Kind,
            "An oversized title value was accepted.");
        Equal(WindowBridgeTitleWriteKind.Rejected, title.Set("three", "123456789").Kind,
            "Repeating an invalid title write did not remain a bounded rejection.");
        Equal(WindowBridgeTitleWriteKind.Rejected, title.WriteChecked("four", null, "Four").Kind,
            "A missing checked baseline performed a direct title write.");
        Equal(WindowBridgeTitleWriteKind.Rejected,
            title.WriteChecked("five", new WindowBridgeTitleBaseline(0, "123456789"), "Five").Kind,
            "An oversized checked baseline entered receipt state.");
        Equal("Two", notes.Title, "Invalid checked writes mutated the Notes title.");
    }

    private static async Task DirectWriteWaitsForCheckedWriteGate()
    {
        string value = "Draft";
        using var checkedSetterEntered = new ManualResetEventSlim();
        using var releaseCheckedSetter = new ManualResetEventSlim();
        using var directRequested = new ManualResetEventSlim();
        var title = new WindowBridgeCheckedTitleField(
            () => value,
            next =>
            {
                if (next == "Checked")
                {
                    checkedSetterEntered.Set();
                    if (!releaseCheckedSetter.Wait(TimeSpan.FromSeconds(2)))
                        throw new TimeoutException("The checked setter was not released.");
                }
                value = next;
            });
        WindowBridgeTitleBaseline baseline = WindowBridgeTitleBaseline.From(title.Snapshot());
        Task<WindowBridgeTitleReceipt> checkedWrite = Task.Factory.StartNew(
            () => title.WriteChecked("checked-gate", baseline, "Checked"),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<WindowBridgeTitleReceipt>? directWrite = null;
        try
        {
            True(checkedSetterEntered.Wait(TimeSpan.FromSeconds(2)), "The checked write did not enter its field gate.");
            directWrite = Task.Factory.StartNew(
                () =>
                {
                    directRequested.Set();
                    return title.Set("direct-gate", "Direct");
                },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            True(directRequested.Wait(TimeSpan.FromSeconds(2)), "The direct write was not requested.");
            True(!directWrite.Wait(TimeSpan.FromMilliseconds(150)),
                "The direct write bypassed the checked write's field gate.");
        }
        finally
        {
            releaseCheckedSetter.Set();
        }

        WindowBridgeTitleReceipt checkedReceipt = await checkedWrite.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        WindowBridgeTitleReceipt directReceipt = await directWrite!.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Equal(WindowBridgeTitleWriteKind.Applied, checkedReceipt.Kind, "The checked write did not apply after the gate released.");
        Equal(WindowBridgeTitleWriteKind.Applied, directReceipt.Kind, "The queued direct write did not apply after the checked write.");
        Equal(1L, checkedReceipt.Current.Version, "The checked write did not receive the first version.");
        Equal(2L, directReceipt.Current.Version, "The direct write did not advance the shared owner version.");
        Equal("Direct", value, "The queued direct write did not become the final title.");
    }

    private static void SetterFailureRetainsTruthfulTerminalReceipt()
    {
        string value = "Draft";
        int setterCalls = 0;
        var title = new WindowBridgeCheckedTitleField(
            () => value,
            next =>
            {
                setterCalls++;
                value = next;
                throw new InvalidOperationException("The setter failed after changing Title.");
            });
        WindowBridgeTitleBaseline baseline = WindowBridgeTitleBaseline.From(title.Snapshot());
        WindowBridgeTitleReceipt first = title.WriteChecked("throw-after-mutation", baseline, "Committed");
        WindowBridgeTitleReceipt retry = title.WriteChecked("throw-after-mutation", baseline, "Committed");

        Equal(WindowBridgeTitleWriteKind.CommittedWithError, first.Kind,
            "A setter that changed Title then threw was reported as a clean rejection.");
        Equal("Committed", first.Current.Value, "The committed failure receipt did not carry the authoritative title.");
        Equal(1L, first.Current.Version, "The committed failure receipt did not advance the title version exactly once.");
        Equal(first, retry, "A committed failure did not retain its terminal receipt for duplicate replay.");
        Equal(1, setterCalls, "A duplicate committed failure invoked the setter again.");

        string unchanged = "Draft";
        int unchangedCalls = 0;
        var rejecting = new WindowBridgeCheckedTitleField(
            () => unchanged,
            _ =>
            {
                unchangedCalls++;
                throw new InvalidOperationException("The title is read-only.");
            });
        WindowBridgeTitleReceipt rejected = rejecting.Set("throw-without-mutation", "Ignored");
        WindowBridgeTitleReceipt rejectedRetry = rejecting.Set("throw-without-mutation", "Ignored");
        Equal(WindowBridgeTitleWriteKind.Rejected, rejected.Kind, "An unchanged throwing setter was not rejected.");
        Equal(new WindowBridgeTitleSnapshot("Draft", 0), rejected.Current,
            "An unchanged throwing setter advanced the authoritative title snapshot.");
        Equal(rejected, rejectedRetry, "An unchanged throwing setter did not retain its rejection receipt.");
        Equal(1, unchangedCalls, "A duplicate rejected setter failure invoked the setter again.");
    }

    private static WindowBridgeAttachment Attach(IWindowBridgeTransport transport, Notes _, string route) =>
        transport.Bind(route, _ => "{}");

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException(message);
    }

    private static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class Notes { internal string Title { get; set; } = string.Empty; }

    private sealed class RecordingTransport : IWindowBridgeTransport
    {
        public WindowBridgeEndpointLease Bind(string route, Func<WindowBridgeArguments, string> __) => WindowBridgeEndpointLease.Direct(route, new Release());
        public WindowBridgeEndpointLease BindAsync(string route, Func<WindowBridgeArguments, CancellationToken, ValueTask<string>> __) => WindowBridgeEndpointLease.Direct(route, new Release());
    }

    private sealed class Release : IDisposable { public void Dispose() { } }
}
