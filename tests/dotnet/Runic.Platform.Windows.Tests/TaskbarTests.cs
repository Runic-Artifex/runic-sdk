using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Runic.Platform.Runtime;
using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

// The NativeTaskbar case sets badges and progress on the taskbar button of a real window, shown on a thread of the
// test's own that pumps its messages, and checks that ITaskbarList3 took each one. What the taskbar draws stays a manual
// check (light and dark, 100, 150 and 200 % scale).
[SupportedOSPlatform("windows8.0")]
internal sealed partial class TaskbarTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Test, Explicit, Category("NativeTaskbar")]
    public async Task NativeTaskbarAsync()
    {
        using var owner = new PumpingOwner("Runic taskbar badge");
        await using var taskbar = new WindowsTaskbar(owner);
        foreach (var badge in new DesktopBadge?[] { new() { Count = 3, Description = "3 unread messages" }, new() { Count = 1234 }, new(), null })
        {
            var result = await taskbar.SetBadgeAsync(badge, CancellationToken.None);
            Check(result is PlatformResult<PlatformUnit>.Success, $"the badge {badge?.ToString() ?? "removal"}: {result}");
        }
        Console.WriteLine("PASS overlay badges: a count with its description, 99+, a dot, and removal");
        foreach (var progress in new DesktopProgress?[] { new(0.25), new(0.5) { State = DesktopProgressState.Paused }, new(1) { State = DesktopProgressState.Error },
            new(null), new(0), null })
        {
            var result = await taskbar.SetProgressAsync(progress, CancellationToken.None);
            Check(result is PlatformResult<PlatformUnit>.Success, $"the progress {progress?.ToString() ?? "removal"}: {result}");
        }
        Console.WriteLine("PASS progress: normal, paused, error, indeterminate, zero and removal");

        // Explorer's TaskbarButtonCreated applies the window's state again, on the window's thread.
        Check(await taskbar.SetBadgeAsync(new() { Count = 7 }, CancellationToken.None) is PlatformResult<PlatformUnit>.Success, "a badge to apply again");
        await owner.InvokeAsync(WindowsPlatformProvider.TaskbarButtonCreated);
        Check(WindowsPlatformProvider.TaskbarButtonCreatedMessage >= 0xC000, $"the registered message: {WindowsPlatformProvider.TaskbarButtonCreatedMessage}");
        Console.WriteLine("PASS the window's badge and progress apply again for TaskbarButtonCreated");

        owner.Dispose();
        Check(await taskbar.SetProgressAsync(new(0.5), CancellationToken.None) is PlatformResult<PlatformUnit>.Unavailable, "a closed window's progress");
        Console.WriteLine("PASS a closed window's taskbar reports the owner closed");
    }

    // A shown top-level window, which has a taskbar button, on a thread that pumps its messages and runs dispatched work.
    private sealed partial class PumpingOwner : INativeWindowOwner, IDisposable
    {
        private const uint WM_APP = 0x8000, WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_VISIBLE = 0x10000000;
        private readonly ConcurrentQueue<Action> _work = new();
        private readonly Thread _thread;
        private readonly TaskCompletionSource<(nint Window, uint Thread)> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _closed;

        internal PumpingOwner(string title)
        {
            _thread = new Thread(() => Run(title)) { IsBackground = true, Name = title };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Check(_ready.Task.Wait(TestTimeouts.Scale(Wait)) && _ready.Task.Result.Window != 0, "the window is shown");
        }

        public Guid Generation { get; } = Guid.NewGuid();
        public bool IsAvailable => !_closed;

        private unsafe void Run(string title)
        {
            nint window = CreateWindowExW(0, "STATIC", title, WS_OVERLAPPEDWINDOW | WS_VISIBLE, 160, 160, 480, 320, 0, 0, 0, 0);
            _ready.TrySetResult((window, GetCurrentThreadId()));
            Msg message;
            while (GetMessageW(&message, 0, 0, 0) > 0)
            {
                if (message.Message == WM_APP && message.Window == 0)
                {
                    while (_work.TryDequeue(out var work)) work();
                    continue;
                }
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
            DestroyWindow(window);
        }

        public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
        {
            if (_closed) throw new OwnerClosedException();
            cancellationToken.ThrowIfCancellationRequested();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Enqueue(() =>
            {
                try { action(_ready.Task.Result.Window); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
            });
            PostThreadMessageW(_ready.Task.Result.Thread, WM_APP, 0, 0);
            return new(done.Task);
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            PostThreadMessageW(_ready.Task.Result.Thread, 0x0012 /* WM_QUIT */, 0, 0);
            _thread.Join(TestTimeouts.Scale(Wait));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public nint Window;
            public uint Message;
            public nuint WParam;
            public nint LParam;
            public uint Time;
            public int X, Y;
        }

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial nint CreateWindowExW(int exStyle, string className, string title, uint style, int x, int y, int width, int height,
            nint parent, nint menu, nint instance, nint parameter);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint window);
        [LibraryImport("user32.dll")] private static unsafe partial int GetMessageW(Msg* message, nint window, uint first, uint last);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static unsafe partial bool TranslateMessage(Msg* message);
        [LibraryImport("user32.dll")] private static unsafe partial nint DispatchMessageW(Msg* message);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);
        [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Taskbar assertion failed: " + message);
    }
}
