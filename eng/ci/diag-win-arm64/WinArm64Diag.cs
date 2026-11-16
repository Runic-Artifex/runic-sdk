#:property TargetFramework=net10.0-windows
#:property UseWindowsForms=true
#:property AllowUnsafeBlocks=true
#:property Nullable=disable
#:property PublishAot=false
#:property BuiltInComInteropSupport=true
#:property TreatWarningsAsErrors=false

// Temporary, Runic-free diagnostics for #373 (Shell_NotifyIcon NIM_ADD) and #391 (DoDragDrop never ends) on the
// hosted windows-11-arm runner. Plain Win32/OLE through P/Invoke and a WinForms Form as a plain Win32 drop target.
//   notify                      Shell_NotifyIcon(NIM_ADD) from a hidden window
//   drag <data> <api> <target>  data: plain (managed IDataObject, CF_HDROP only) | shell (BHID_DataObject) |
//                                     shcreate (SHCreateDataObject + CF_HDROP, Runic's drag-out path)
//                               api:  ole (DoDragDrop) | sh (SHDoDragDrop)
//                               target: form (WinForms drop target) | desktop (Explorer's desktop)
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;

internal static class Program
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    internal static void Log(string text) => Console.WriteLine($"[{Clock.ElapsedMilliseconds,6} ms t{Environment.CurrentManagedThreadId,-2}] {text}");

    [STAThread]
    private static int Main(string[] args)
    {
        Log($"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}, session {Process.GetCurrentProcess().SessionId}, input desktop '{Native.InputDesktopName()}'");
        return args[0] switch
        {
            "notify" => Notify.Run(),
            "drag" => DragCase.Run(args[1], args[2], args[3]),
            "probe" => Native.Probe(),
            "dismiss" => Native.Dismiss(),
            _ => 2,
        };
    }
}

internal static class Notify
{
    internal static int Run()
    {
        var tray = Native.FindWindow("Shell_TrayWnd", null);
        var notifyArea = tray == 0 ? 0 : Native.FindWindowEx(tray, 0, "TrayNotifyWnd", null);
        Native.GetWindowThreadProcessId(tray, out var explorer);
        Program.Log($"Shell_TrayWnd {tray:X}, TrayNotifyWnd {notifyArea:X}, owner pid {explorer}");
        using var form = new Form { ShowInTaskbar = false };
        var window = form.Handle;
        var data = new Native.NotifyIconData
        {
            Size = Marshal.SizeOf<Native.NotifyIconData>(), Window = window, Id = 1,
            Flags = 0x1 | 0x2 | 0x4, // NIF_MESSAGE | NIF_ICON | NIF_TIP
            CallbackMessage = 0x8001, Icon = Native.LoadIcon(0, 32512), Tip = "runic diag",
        };
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var watch = Stopwatch.StartNew();
            Marshal.SetLastPInvokeError(0);
            var added = Native.Shell_NotifyIcon(0, ref data); // NIM_ADD
            var error = Marshal.GetLastPInvokeError();
            Program.Log($"NIM_ADD attempt {attempt}: {added}, last error 0x{error:X8} ({new System.ComponentModel.Win32Exception(error).Message}), {watch.ElapsedMilliseconds} ms");
            if (added)
            {
                data.TimeoutOrVersion = 4;
                Program.Log($"NIM_SETVERSION 4: {Native.Shell_NotifyIcon(4, ref data)}");
                Program.Log($"NIM_DELETE: {Native.Shell_NotifyIcon(2, ref data)}");
                Console.WriteLine("NOTIFY RESULT: added");
                return 0;
            }
            Thread.Sleep(2000);
        }
        Console.WriteLine("NOTIFY RESULT: refused");
        return 1;
    }
}

internal static class DragCase
{
    private static int queryCalls, feedbackCalls, dragOvers;
    private static volatile bool ended;

    internal static int Run(string data, string api, string target)
    {
        Program.Log($"Case data={data} api={api} target={target}");
        var form = new Form
        {
            Text = "diag drop target", StartPosition = FormStartPosition.Manual, Location = new(300, 200), Size = new(480, 360),
            TopMost = true, AllowDrop = true,
        };
        form.DragEnter += (_, e) => { Program.Log($"target DragEnter at {e.X},{e.Y}, formats [{string.Join(", ", e.Data.GetFormats())}]"); e.Effect = DragDropEffects.Copy; };
        form.DragOver += (_, e) => { if (Interlocked.Increment(ref dragOvers) is 1 or 10 or 50) Program.Log($"target DragOver #{dragOvers} at {e.X},{e.Y}"); e.Effect = DragDropEffects.Copy; };
        form.DragLeave += (_, _) => Program.Log("target DragLeave");
        form.DragDrop += (_, e) => Program.Log($"target DragDrop: [{string.Join(", ", (string[])e.Data.GetData(DataFormats.FileDrop) ?? [])}]");
        var root = Directory.CreateTempSubdirectory("diag-drop-");
        var file = Path.Combine(root.FullName, "dropped notes.txt");
        File.WriteAllText(file, "dropped");
        var paths = new[] { file };
        var result = 1;
        form.Shown += async (_, _) =>
        {
            await Task.Delay(500);
            var bounds = form.Bounds;
            var start = (X: bounds.Left - 60, Y: bounds.Top + 40);
            var end = target == "form" ? (X: bounds.Left + bounds.Width / 2, Y: bounds.Top + bounds.Height / 2) : (X: 150, Y: 680);
            var thread = new Thread(() =>
            {
                try { result = Drag(data, api, paths, start, end); }
                catch (Exception error) { Program.Log($"drag thread failed: {error}"); result = 4; }
                finally { ended = true; form.BeginInvoke(() => form.Close()); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            var watchdog = new Thread(() => Watchdog(thread)) { IsBackground = true };
            watchdog.Start();
        };
        Application.Run(form);
        Program.Log($"DRAG RESULT {data}/{api}/{target}: exit {result}, QueryContinueDrag {queryCalls}, GiveFeedback {feedbackCalls}, target DragOver {dragOvers}");
        try { root.Delete(true); } catch { }
        return result;
    }

    private static void Watchdog(Thread drag)
    {
        var watch = Stopwatch.StartNew();
        while (!ended && watch.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(100);
        if (ended) return;
        Program.Log($"HANG: the drag did not end within 20 s; QueryContinueDrag {queryCalls}, GiveFeedback {feedbackCalls}, target DragOver {dragOvers}, cursor {Native.Cursor()}, VK_LBUTTON down {(Native.GetAsyncKeyState(1) & 0x8000) != 0}, foreground {Native.GetForegroundWindow():X}");
        Native.DumpStacks();
        Native.Escape();
        Program.Log("sent Escape");
        watch.Restart();
        while (!ended && watch.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(100);
        Program.Log($"after Escape: ended={ended}, QueryContinueDrag {queryCalls}");
        Console.WriteLine($"DRAG RESULT: hang, QueryContinueDrag {queryCalls}, GiveFeedback {feedbackCalls}, target DragOver {dragOvers}");
        Console.Out.Flush();
        Process.GetCurrentProcess().Kill();
    }

    private static int Drag(string data, string api, string[] paths, (int X, int Y) start, (int X, int Y) end)
    {
        Program.Log($"OleInitialize 0x{Native.OleInitialize(0):X8}");
        var dataObject = data switch
        {
            "plain" => Marshal.GetComInterfaceForObject(new PlainDataObject(paths), typeof(System.Runtime.InteropServices.ComTypes.IDataObject)),
            "shell" => Native.ShellItemArrayDataObject(paths),
            "shcreate" => Native.ShCreateDataObject(paths),
            _ => throw new ArgumentException(data),
        };
        var source = Marshal.GetComInterfaceForObject(new DropSource(), typeof(IDropSource));
        var helper = Native.CreateWindowEx(0x8 | 0x80, "STATIC", "diag drag source", 0x90000000, start.X - 20, start.Y - 20, 40, 40, 0, 0, 0, 0);
        Native.SetForegroundWindow(helper);
        Native.Pump(300);
        Native.Mouse(start.X, start.Y, Native.MouseMove);
        Native.Pump(200);
        Native.Mouse(start.X, start.Y, Native.MouseLeftDown);
        var pressed = Stopwatch.StartNew();
        while (Native.GetKeyState(1) >= 0 && pressed.ElapsedMilliseconds < 3000) Native.Pump(20);
        Program.Log($"button down seen by the drag thread: {Native.GetKeyState(1) < 0} after {pressed.ElapsedMilliseconds} ms; cursor {Native.Cursor()}; window under start {Native.WindowAt(start)}, under end {Native.WindowAt(end)}");
        var mover = Task.Run(async () =>
        {
            await Task.Delay(300);
            for (var step = 1; step <= 30; step++)
            {
                Native.Mouse(start.X + (end.X - start.X) * step / 30, start.Y + (end.Y - start.Y) * step / 30, Native.MouseMove);
                if (step is 1 or 15 or 30) Program.Log($"mover step {step}: cursor {Native.Cursor()}, QueryContinueDrag {queryCalls}");
                await Task.Delay(30);
            }
            for (var wiggle = 0; wiggle < 10; wiggle++) { Native.Mouse(end.X + wiggle % 2, end.Y, Native.MouseMove); await Task.Delay(50); }
            await Task.Delay(500);
            Native.Mouse(end.X, end.Y, Native.MouseLeftUp);
            Program.Log($"mover released: VK_LBUTTON down {(Native.GetAsyncKeyState(1) & 0x8000) != 0}, QueryContinueDrag {queryCalls}");
        });
        Program.Log($"calling {(api == "sh" ? "SHDoDragDrop" : "DoDragDrop")}");
        int hr, effect;
        if (api == "sh") { hr = Native.SHDoDragDrop(helper, dataObject, source, 1, out var e); effect = e; }
        else hr = Native.DoDragDrop(dataObject, source, 1, out effect);
        Program.Log($"drag returned 0x{hr:X8}, effect {effect}");
        mover.Wait();
        Native.DestroyWindow(helper);
        Console.WriteLine($"DRAG RESULT: ended 0x{hr:X8} effect {effect}, QueryContinueDrag {queryCalls}, GiveFeedback {feedbackCalls}, target DragOver {dragOvers}");
        return hr == 0x00040100 ? 0 : 1;
    }

    [ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag(int escapePressed, uint keyState);
        [PreserveSig] int GiveFeedback(uint effect);
    }

    [ComVisible(true)]
    internal sealed class DropSource : IDropSource
    {
        public int QueryContinueDrag(int escapePressed, uint keyState)
        {
            var calls = Interlocked.Increment(ref queryCalls);
            if (calls == 1 || calls % 20 == 0) Program.Log($"QueryContinueDrag #{calls} escape={escapePressed} keys=0x{keyState:X}");
            return escapePressed != 0 ? 0x00040101 : (keyState & 1) == 0 ? 0x00040100 : 0;
        }

        public int GiveFeedback(uint effect)
        {
            if (Interlocked.Increment(ref feedbackCalls) == 1) Program.Log($"GiveFeedback #1 effect {effect}");
            return 0x00040102;
        }
    }

    // CF_HDROP only, no shell formats, no drag image.
    [ComVisible(true)]
    internal sealed class PlainDataObject(string[] paths) : System.Runtime.InteropServices.ComTypes.IDataObject
    {
        private const short FileDrop = 15;

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            Program.Log($"IDataObject.GetData cf={(ushort)format.cfFormat} {Native.FormatName(format.cfFormat)} tymed={format.tymed}");
            if (format.cfFormat != FileDrop || (format.tymed & TYMED.TYMED_HGLOBAL) == 0) throw new COMException(null, unchecked((int)0x80040064)); // DV_E_FORMATETC
            medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = Native.DropFiles(paths) };
        }

        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException(null, unchecked((int)0x80004001));

        public int QueryGetData(ref FORMATETC format)
        {
            Program.Log($"IDataObject.QueryGetData cf={(ushort)format.cfFormat} {Native.FormatName(format.cfFormat)}");
            return format.cfFormat == FileDrop && (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? 0 : unchecked((int)0x80040064);
        }

        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) { formatOut = formatIn; formatOut.ptd = 0; return 0x00040130; }

        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
        {
            Program.Log($"IDataObject.SetData cf={(ushort)formatIn.cfFormat} {Native.FormatName(formatIn.cfFormat)} (refused)");
            throw new COMException(null, unchecked((int)0x80004001));
        }

        public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
        {
            Program.Log($"IDataObject.EnumFormatEtc {direction}");
            if (direction != DATADIR.DATADIR_GET) throw new COMException(null, unchecked((int)0x80004001));
            return new FormatEnumerator([new FORMATETC { cfFormat = FileDrop, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL }]);
        }

        public int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) => throw new COMException(null, unchecked((int)0x80040003));
        public int EnumDAdvise(out IEnumSTATDATA enumAdvise) { enumAdvise = null; return unchecked((int)0x80040003); }
    }

    [ComVisible(true)]
    internal sealed class FormatEnumerator(FORMATETC[] formats, int index = 0) : IEnumFORMATETC
    {
        private int position = index;

        public int Next(int count, FORMATETC[] output, int[] fetched)
        {
            var copied = 0;
            while (copied < count && position < formats.Length) output[copied++] = formats[position++];
            if (fetched != null) fetched[0] = copied;
            return copied == count ? 0 : 1;
        }

        public int Skip(int count) { position += count; return position <= formats.Length ? 0 : 1; }
        public int Reset() { position = 0; return 0; }
        public void Clone(out IEnumFORMATETC clone) => clone = new FormatEnumerator(formats, position);
    }
}

internal static unsafe partial class Native
{
    internal const uint MouseMove = 0x0001 | 0x8000;
    internal const uint MouseLeftDown = 0x0002 | 0x8000;
    internal const uint MouseLeftUp = 0x0004 | 0x8000;

    internal static string Cursor() => GetCursorPos(out var point) ? $"{point.X},{point.Y}" : "?";

    internal static string WindowAt((int X, int Y) point)
    {
        var window = WindowFromPoint(new Point { X = point.X, Y = point.Y });
        var name = new char[256];
        var length = GetClassName(window, name, name.Length);
        GetWindowThreadProcessId(window, out var pid);
        return $"{window:X} '{new string(name, 0, length)}' pid {pid} {ProcessName(pid)} {Describe(window)}";
    }

    private static string ProcessName(uint pid)
    {
        try { using var process = Process.GetProcessById((int)pid); return $"{process.ProcessName} (started {process.StartTime:HH:mm:ss}, suspended threads {process.Threads.Cast<ProcessThread>().Count(static t => t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == ThreadWaitReason.Suspended)}/{process.Threads.Count})"; }
        catch (Exception error) { return $"? ({error.GetType().Name})"; }
    }

    private static string Describe(nint window)
    {
        GetWindowRect(window, out var bounds);
        int cloaked = 0;
        _ = DwmGetWindowAttribute(window, 14, &cloaked, 4); // DWMWA_CLOAKED
        var title = new char[256];
        var length = GetWindowText(window, title, title.Length);
        var exStyle = GetWindowLongPtr(window, -20);
        return $"'{new string(title, 0, length)}' [{bounds.Left},{bounds.Top} {bounds.Right - bounds.Left}x{bounds.Bottom - bounds.Top}] visible {IsWindowVisible(window)} cloaked 0x{cloaked:X} exstyle 0x{exStyle:X} hung {IsHungAppWindow(window)}";
    }

    // The visible top-level windows in z-order, the windows under the smoke's points and the foreground window.
    internal static int Probe()
    {
        Program.Log($"foreground {WindowAt0(GetForegroundWindow())}");
        foreach (var point in new[] { (240, 240), (540, 380), (150, 680), (512, 384) }) Program.Log($"under {point}: {WindowAt(point)}");
        var index = 0;
        EnumWindows((window, _) =>
        {
            if (IsWindowVisible(window))
            {
                var name = new char[256];
                var length = GetClassName(window, name, name.Length);
                GetWindowThreadProcessId(window, out var pid);
                Program.Log($"z{index++,-3} {window:X} '{new string(name, 0, length)}' pid {pid} {ProcessName(pid)} {Describe(window)}");
            }
            return index < 60;
        }, 0);
        return 0;
    }

    // Stops the process that owns the window at the screen centre unless it is Explorer.
    internal static int Dismiss()
    {
        var window = WindowFromPoint(new Point { X = 512, Y = 384 });
        GetWindowThreadProcessId(window, out var pid);
        using var process = Process.GetProcessById((int)pid);
        Program.Log($"window at the screen centre: {WindowAt((512, 384))}, path {process.MainModule?.FileName}");
        if (process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return 0;
        process.Kill();
        process.WaitForExit(10_000);
        Program.Log($"stopped {pid}");
        return 0;
    }

    private static string WindowAt0(nint window)
    {
        var name = new char[256];
        var length = GetClassName(window, name, name.Length);
        GetWindowThreadProcessId(window, out var pid);
        return $"{window:X} '{new string(name, 0, length)}' pid {pid} {ProcessName(pid)} {Describe(window)}";
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect bounds);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, char[] text, int length);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, void* value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left; internal int Top; internal int Right; internal int Bottom; }

    internal static string InputDesktopName()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS
        if (desktop == 0) return $"none (error {Marshal.GetLastPInvokeError()})";
        var buffer = new char[256];
        GetUserObjectInformation(desktop, 2, buffer, buffer.Length * 2, out _);
        CloseDesktop(desktop);
        return new string(buffer).TrimEnd('\0');
    }

    internal static string FormatName(short format)
    {
        var buffer = new char[128];
        var length = GetClipboardFormatName((ushort)format, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "(standard)";
    }

    internal static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        var message = stackalloc byte[64];
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            while (PeekMessage(message, 0, 0, 0, 1) != 0) { TranslateMessage(message); DispatchMessage(message); }
            Thread.Sleep(5);
        }
    }

    internal static void Mouse(int x, int y, uint flags)
    {
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = Math.Max(1, GetSystemMetrics(78) - 1);
        var height = Math.Max(1, GetSystemMetrics(79) - 1);
        var input = new Input { Type = 0, Mouse = new MouseInput { X = (int)((x - left) * 65535L / width), Y = (int)((y - top) * 65535L / height), Flags = flags | 0x4000 } };
        if (SendInput(1, &input, sizeof(Input)) != 1) Program.Log($"SendInput refused: {Marshal.GetLastPInvokeError()}");
    }

    internal static void Escape()
    {
        var inputs = stackalloc Input[2];
        inputs[0] = new Input { Type = 1 };
        inputs[1] = new Input { Type = 1 };
        ((ushort*)&inputs[0].Mouse)[0] = 0x1B;
        ((ushort*)&inputs[1].Mouse)[0] = 0x1B;
        ((uint*)&inputs[1].Mouse)[1] = 0x0002;
        SendInput(2, inputs, sizeof(Input));
    }

    // A minidump of this process (all thread stacks) into DIAG_DUMP; the workflow reads its stacks with cdb -z.
    internal static void DumpStacks()
    {
        var path = Environment.GetEnvironmentVariable("DIAG_DUMP");
        if (string.IsNullOrEmpty(path)) return;
        using var file = new FileStream(path, FileMode.Create);
        using var self = Process.GetCurrentProcess();
        // MiniDumpWithThreadInfo | MiniDumpWithUnloadedModules | MiniDumpWithHandleData | MiniDumpWithProcessThreadData
        var written = MiniDumpWriteDump(self.Handle, (uint)Environment.ProcessId, file.SafeFileHandle.DangerousGetHandle(), 0x1000 | 0x20 | 0x4 | 0x100, 0, 0, 0);
        Program.Log($"minidump {path}: {written} (error {Marshal.GetLastPInvokeError()})");
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(nint process, uint processId, nint file, uint type, nint exception, nint user, nint callback);

    internal static nint DropFiles(string[] paths)
    {
        var text = string.Concat(paths.Select(static path => path + '\0')) + '\0';
        var bytes = new byte[20 + text.Length * 2];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 20);
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 4), 1);
        System.Text.Encoding.Unicode.GetBytes(text, bytes.AsSpan(20));
        var memory = GlobalAlloc(0x0002, (nuint)bytes.Length);
        Marshal.Copy(bytes, 0, GlobalLock(memory), bytes.Length);
        GlobalUnlock(memory);
        return memory;
    }

    private static nint[] Pidls(string[] paths)
    {
        var pidls = new nint[paths.Length];
        for (var index = 0; index < paths.Length; index++) Marshal.ThrowExceptionForHR(SHParseDisplayName(paths[index], 0, out pidls[index], 0, out _));
        return pidls;
    }

    internal static nint ShellItemArrayDataObject(string[] paths)
    {
        var pidls = Pidls(paths);
        nint array;
        fixed (nint* items = pidls) Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)pidls.Length, items, out array));
        var handler = new Guid("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4");
        var iid = new Guid("0000010e-0000-0000-C000-000000000046");
        nint dataObject;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, Guid*, nint*, int>)(*(nint**)array)[3])(array, 0, &handler, &iid, &dataObject));
        return dataObject;
    }

    internal static nint ShCreateDataObject(string[] paths)
    {
        var pidls = Pidls(paths);
        var iid = new Guid("0000010e-0000-0000-C000-000000000046");
        nint dataObject;
        fixed (nint* items = pidls) Marshal.ThrowExceptionForHR(SHCreateDataObject(0, (uint)pidls.Length, items, 0, &iid, &dataObject));
        var format = new FormatEtc { Format = 15, Aspect = 1, Index = -1, Tymed = 1 };
        var medium = new StgMedium { Tymed = 1, Handle = DropFiles(paths) };
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, FormatEtc*, StgMedium*, int, int>)(*(nint**)dataObject)[7])(dataObject, &format, &medium, 1));
        return dataObject;
    }

    [StructLayout(LayoutKind.Sequential)] private struct FormatEtc { internal ushort Format; internal nint TargetDevice; internal uint Aspect; internal int Index; internal uint Tymed; }
    [StructLayout(LayoutKind.Sequential)] private struct StgMedium { internal uint Tymed; internal nint Handle; internal nint Release; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X; internal int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { internal int X; internal int Y; internal uint Data; internal uint Flags; internal uint Time; internal nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct Input { [FieldOffset(0)] internal uint Type; [FieldOffset(8)] internal MouseInput Mouse; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        internal int Size;
        internal nint Window;
        internal uint Id;
        internal uint Flags;
        internal uint CallbackMessage;
        internal nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State;
        internal uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid Item;
        internal nint BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("ole32.dll")] internal static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] internal static extern int DoDragDrop(nint dataObject, nint dropSource, int okEffects, out int effect);
    [DllImport("shell32.dll")] internal static extern int SHDoDragDrop(nint window, nint dataObject, nint dropSource, int okEffects, out int effect);
    [DllImport("shell32.dll")] private static extern int SHCreateDataObject(nint folder, uint count, nint* items, nint inner, Guid* iid, nint* dataObject);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint attributes, out uint result);
    [DllImport("shell32.dll")] private static extern int SHCreateShellItemArrayFromIDLists(uint count, nint* pidls, out nint array);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input* inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern short GetKeyState(int key);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, char[] name, int length);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindow(string className, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindowEx(nint parent, nint after, string className, string name);
    [DllImport("user32.dll")] internal static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern int PeekMessage(byte* message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern int TranslateMessage(byte* message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(byte* message);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint handle, int index, char[] buffer, int length, out int needed);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClipboardFormatName(uint format, char[] name, int length);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
}
