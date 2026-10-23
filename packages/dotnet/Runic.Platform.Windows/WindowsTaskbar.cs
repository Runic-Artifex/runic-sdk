using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Runic.Platform.Runtime;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Runic.Platform.Windows;

// Badge and progress on a window's taskbar button through ITaskbarList3, on the window's thread: the badge as an overlay
// icon Runic draws (WindowsBadgeGlyph) at the window's DPI, with the badge's description as its accessible text. Each
// window's state is kept, so the host can apply it again when Explorer sends TaskbarButtonCreated, as it does when the
// button appears and after Explorer restarts.
[SupportedOSPlatform("windows8.0")]
internal sealed class WindowsTaskbar(INativeWindowOwner owner) : IWindowTaskbar
{
    private static readonly ConcurrentDictionary<nint, State> s_states = new();
    private static readonly Lazy<uint> s_buttonCreated = new(static () => PInvoke.RegisterWindowMessage("TaskbarButtonCreated"));
    private nint _window;

    private sealed class State
    {
        internal DesktopBadge? Badge;
        internal DesktopProgress? Progress;
    }

    // The registered TaskbarButtonCreated message, which the host's window procedure forwards to ButtonCreated.
    internal static uint ButtonCreatedMessage => s_buttonCreated.Value;

    public ValueTask<PlatformResult<PlatformUnit>> SetBadgeAsync(DesktopBadge? badge, CancellationToken cancellationToken) =>
        InvokeAsync(window =>
        {
            s_states.GetOrAdd(window, static _ => new()).Badge = badge;
            return Result(window, ApplyBadge(window, badge));
        }, cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetProgressAsync(DesktopProgress? progress, CancellationToken cancellationToken) =>
        InvokeAsync(window =>
        {
            s_states.GetOrAdd(window, static _ => new()).Progress = progress;
            return Result(window, ApplyProgress(window, progress));
        }, cancellationToken);

    // Applies the window's badge and progress again; on the window's thread.
    internal static void ButtonCreated(nint window)
    {
        if (!s_states.TryGetValue(window, out var state)) return;
        _ = ApplyBadge(window, state.Badge);
        _ = ApplyProgress(window, state.Progress);
    }

    // The window and its button go together; a window handle may be used again by another window later.
    public ValueTask DisposeAsync()
    {
        var window = Interlocked.Exchange(ref _window, 0);
        if (window != 0) s_states.TryRemove(window, out _);
        return ValueTask.CompletedTask;
    }

    private async ValueTask<PlatformResult<PlatformUnit>> InvokeAsync(Func<nint, PlatformResult<PlatformUnit>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.IsAvailable) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        var generation = owner.Generation;
        PlatformResult<PlatformUnit> result = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        try
        {
            await owner.InvokeAsync(handle =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (handle == 0 || !owner.IsAvailable || owner.Generation != generation) return;
                if (Interlocked.Exchange(ref _window, handle) != handle)
                    // An elevated window still hears Explorer, which runs at medium integrity.
                    _ = PInvoke.ChangeWindowMessageFilterEx((HWND)handle, ButtonCreatedMessage, WINDOW_MESSAGE_FILTER_ACTION.MSGFLT_ALLOW);
                result = action(handle);
            }, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OwnerClosedException) { return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (ObjectDisposedException) { return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
    }

    // A window without its taskbar button yet, such as one not shown, takes the state when the button appears.
    private static PlatformResult<PlatformUnit> Result(nint window, int hr) =>
        hr >= 0 || !PInvoke.IsWindowVisible((HWND)window) ? new PlatformResult<PlatformUnit>.Success(new PlatformUnit())
        : new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Other, PlatformDiagnostic.FromHResult(hr));

    private static unsafe int ApplyBadge(nint window, DesktopBadge? badge)
    {
        HICON icon = default;
        try
        {
            if (badge is not null)
            {
                uint dpi = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393) ? PInvoke.GetDpiForWindow((HWND)window) : 96;
                int size = Math.Max(16, (int)Math.Round(16 * (dpi == 0 ? 96 : dpi) / 96.0));
                icon = CreateIcon(WindowsBadgeGlyph.Render(DesktopTaskbarRules.Label(badge), size), size);
                if (icon.IsNull) return unchecked((int)0x80004005);
            }
            string? description = badge is null ? null : badge.Description ?? DesktopTaskbarRules.Label(badge);
            return WithTaskbar(taskbar =>
            {
                fixed (char* text = description) return taskbar->SetOverlayIcon((HWND)window, icon, text).Value;
            });
        }
        finally
        {
            // The taskbar keeps its own copy of the overlay.
            if (!icon.IsNull) PInvoke.DestroyIcon(icon);
        }
    }

    private static unsafe int ApplyProgress(nint window, DesktopProgress? progress) => WithTaskbar(taskbar =>
    {
        var hwnd = (HWND)window;
        if (progress is null) return taskbar->SetProgressState(hwnd, TBPFLAG.TBPF_NOPROGRESS).Value;
        if (progress.Value is not { } value) return taskbar->SetProgressState(hwnd, TBPFLAG.TBPF_INDETERMINATE).Value;
        // SetProgressValue leaves an indeterminate bar; the state then colours it.
        int hr = taskbar->SetProgressValue(hwnd, (ulong)Math.Round(value * 10_000), 10_000).Value;
        return hr < 0 ? hr : taskbar->SetProgressState(hwnd, progress.State switch
        {
            DesktopProgressState.Paused => TBPFLAG.TBPF_PAUSED,
            DesktopProgressState.Error => TBPFLAG.TBPF_ERROR,
            _ => TBPFLAG.TBPF_NORMAL,
        }).Value;
    });

    private static unsafe int WithTaskbar(TaskbarCall call)
    {
        Guid clsid = new(0x56FDF344, 0xFD6D, 0x11D0, 0x95, 0x8A, 0x00, 0x60, 0x97, 0xC9, 0xA0, 0x90); // CLSID_TaskbarList
        Guid iid = typeof(ITaskbarList3).GUID;
        ITaskbarList3* taskbar = null;
        int hr = PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&taskbar).Value;
        if (hr < 0) return hr;
        try
        {
            hr = taskbar->HrInit().Value;
            return hr < 0 ? hr : call(taskbar);
        }
        finally { taskbar->Release(); }
    }

    private unsafe delegate int TaskbarCall(ITaskbarList3* taskbar);

    // A 32-bit icon with alpha from top-down BGRA pixels.
    private static unsafe HICON CreateIcon(byte[] pixels, int size)
    {
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = size,
                biHeight = -size,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };
        void* bits = null;
        HBITMAP color = PInvoke.CreateDIBSection(default, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, default, 0);
        HBITMAP mask = PInvoke.CreateBitmap(size, size, 1, 1, null);
        try
        {
            if (color.IsNull || mask.IsNull || bits is null) return default;
            pixels.CopyTo(new Span<byte>(bits, pixels.Length));
            var icon = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
            return PInvoke.CreateIconIndirect(&icon);
        }
        finally
        {
            if (!color.IsNull) PInvoke.DeleteObject((HGDIOBJ)color.Value);
            if (!mask.IsNull) PInvoke.DeleteObject((HGDIOBJ)mask.Value);
        }
    }
}
